using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using PropertyGpsApi;
using PropertyGpsApi.Common;
using PropertyGpsApi.Features.Auth;
using PropertyGpsApi.Features.Masters;
using PropertyGpsApi.Features.Properties;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// A git-ignored local overrides file, layered on top of appsettings.json.
//
// User secrets are the usual answer for developer credentials, but they only load in the
// Development environment AND only for the Windows profile that created them, which makes
// them quietly invisible when the app is launched from an IDE running as another user, or
// from the published exe. This file has none of those failure modes. It is listed in
// .gitignore and must never be committed.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

if (!StartupChecks.TryEnsureConfigured(builder, out var configurationError))
{
    Console.Error.WriteLine(configurationError);
    return 1;
}

// ---- Options -------------------------------------------------------------
// Everything is ValidateOnStart. A missing connection string or a placeholder JWT key
// must stop the process at deploy time, not surface as a 500 on the first field request
// of the morning.
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.Section))
    .ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddOptions<StoredProcedureOptions>()
    .Bind(builder.Configuration.GetSection(StoredProcedureOptions.Section))
    .ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddOptions<OtpOptions>()
    .Bind(builder.Configuration.GetSection(OtpOptions.Section))
    .ValidateDataAnnotations().ValidateOnStart();

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.Section))
    .ValidateDataAnnotations()
    .Validate(o => o.KeyIsStrongEnough, "Jwt:Key must be at least 32 bytes for HS256.")
    .Validate(o => !o.IsPlaceholder, "Jwt:Key is still the placeholder. Set a real key via user-secrets or an environment variable.")
    .ValidateOnStart();

// ---- Services ------------------------------------------------------------
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

builder.Services.AddScoped<IOfficerRepository, OfficerRepository>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IPropertyRepository, PropertyRepository>();
builder.Services.AddSingleton<IAssignmentReader, AssignmentReader>();
builder.Services.AddScoped<IMasterRepository, MasterRepository>();
builder.Services.AddScoped<IPushStatusRepository, PushStatusRepository>();

// The OTP sender seam. DevelopmentOtpSender writes the code to the log, which is the whole
// point of it, and exactly why selecting it outside Development must fail the process
// rather than quietly leak authentication codes into a log file.
var otpSender = builder.Configuration["Otp:Sender"] ?? "Development";
switch (otpSender)
{
    case "Development":
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException(
                "Otp:Sender=Development logs OTPs in clear text and may only be used in Development.");
        builder.Services.AddSingleton<IOtpSender, DevelopmentOtpSender>();
        break;
    case "SmsGateway":
        builder.Services.AddOptions<SmsOptions>()
            .Bind(builder.Configuration.GetSection(SmsOptions.Section))
            .ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddHttpClient<IOtpSender, SmsGatewayOtpSender>((sp, client) =>
        {
            var sms = sp.GetRequiredService<IOptions<SmsOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(sms.TimeoutSeconds);
        });
        break;
    default:
        throw new InvalidOperationException("Unknown Otp:Sender value: " + otpSender);
}

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddControllers()
    .AddJsonOptions(json =>
    {
        // Pinned to Never: null means "not answered" on this contract and is distinct
        // from 0. Omitting the key would collapse the two.
        json.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        json.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    });

// Both settings are required for the promise that the client never sees ProblemDetails:
// the [ApiController] validation 400 never reaches the exception handler.
builder.Services.Configure<ApiBehaviorOptions>(api =>
{
    api.SuppressMapClientErrors = true;
    api.InvalidModelStateResponseFactory = ctx =>
        new ObjectResult(ErrorEnvelopeWriter.FromModelState(ctx.ModelState, ctx.HttpContext))
        { StatusCode = StatusCodes.Status400BadRequest };
});

builder.Services.AddOpenApi(o => o.AddDocumentTransformer<OpenApiJwtTransformer>());

// ---- Authentication ------------------------------------------------------
var jwtOptions = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>()
    ?? throw new InvalidOperationException("The Jwt configuration section is missing.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Keep the sub claim spelled "sub". The default inbound mapping rewrites it to a
        // long WS-Federation URI and every claim lookup in the codebase then reads wrong.
        o.MapInboundClaims = false;

        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(jwtOptions.KeyBytes),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.Zero,
            NameClaimType = "name"
        };

        o.Events = new JwtBearerEvents
        {
            // The framework default is an empty body. The mobile client must receive our
            // envelope, and critically must see retryable=true so an expired token sends
            // the officer back to sign-in instead of parking their survey as failed.
            OnChallenge = ctx =>
            {
                ctx.HandleResponse();
                return ErrorEnvelopeWriter.WriteAsync(
                    ctx.HttpContext, StatusCodes.Status401Unauthorized,
                    ApiErrorCodes.AuthRequired,
                    "Your session has expired. Please sign in again.",
                    retryable: true);
            },
            OnForbidden = ctx => ErrorEnvelopeWriter.WriteAsync(
                ctx.HttpContext, StatusCodes.Status403Forbidden,
                ApiErrorCodes.Forbidden,
                "You are not authorised for this record.")
        };
    });

// Secure by default: an endpoint needs a token unless it says [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ---- Rate limiting -------------------------------------------------------
// Partitioned by IP, not by mobile number: a partitioner must not read the request body.
// This is a cheap outer shield against a flood; the per-officer lockout that actually
// matters already lives inside USP_S_Officer_ValidateOTP.
builder.Services.AddRateLimiter(rl =>
{
    rl.AddPolicy(RateLimitPolicies.OtpSend, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            }));

    rl.AddPolicy(RateLimitPolicies.OtpVerify, http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0
            }));

    rl.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();

        await ErrorEnvelopeWriter.WriteAsync(
            ctx.HttpContext, StatusCodes.Status429TooManyRequests,
            ApiErrorCodes.TooManyRequests,
            "Too many attempts. Please wait a moment and try again.",
            retryable: true);
    };
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<SqlServerHealthCheck>("sql", tags: ["ready"]);

var app = builder.Build();

// ---- Pipeline (order matters) --------------------------------------------
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // A fallback is mandatory even with an IExceptionHandler registered. It writes our
    // envelope rather than pulling in AddProblemDetails(), so there is no path at all by
    // which the mobile client can receive an RFC 9457 body it cannot parse.
    ExceptionHandler = ctx => ErrorEnvelopeWriter.WriteAsync(
        ctx, StatusCodes.Status500InternalServerError, ApiErrorCodes.ServerError,
        "Something went wrong at our end.", retryable: true)
});
// Catches responses that never reach MVC at all - the routing 404, a 405, a 415 - which
// would otherwise return an empty body the client cannot parse.
app.UseStatusCodePages(ErrorEnvelopeWriter.StatusCodeHandler);

// Swagger UI over the document MapOpenApi serves below. Development only, for the reason
// given there. Mounted before UseRouting deliberately: as ordinary middleware it never
// enters endpoint routing, so neither the fallback authorization policy nor the
// MapFallback catch-all applies - otherwise /swagger would answer with our "endpoint does
// not exist" envelope instead of the UI.
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI(ui =>
    {
        ui.SwaggerEndpoint("/openapi/v1.json", "PropertyGpsApi v1");
        ui.RoutePrefix = "swagger";
        ui.DocumentTitle = "PropertyGpsApi";
    });
}

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("live")
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready")
}).AllowAnonymous();

// An unmatched URL must read as a 404, not a 401. The fallback authorization policy
// also applies to requests that match no endpoint, so without this a typo in the base
// URL looks to the client exactly like an expired token - and since 401 is reported
// retryable, the app would sit there re-authenticating against a route that will never
// exist. An anonymous catch-all gives it the honest answer.
//
// The explicit "{*path}" matters: MapFallback defaults to "{*path:nonfile}", which skips
// any URL whose last segment contains a dot. Without it /openapi/v1.json - and every
// file-looking typo - matched no endpoint at all and came back 401 rather than 404.
app.MapFallback("{*path}", context => ErrorEnvelopeWriter.WriteAsync(
    context, StatusCodes.Status404NotFound, ApiErrorCodes.NotFound,
    "The requested endpoint does not exist.")).AllowAnonymous();

// Dev only: an unauthenticated OpenAPI document on a government-facing host enumerates
// every endpoint and field name for free.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.Run();
return 0;

public partial class Program;
