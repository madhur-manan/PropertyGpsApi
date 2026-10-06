using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using PropertyGpsApi.Common;
using PropertyGpsApi.Controllers;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Infrastructure.Security.BodyEncryption;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using static PropertyGpsApi.Tests.FilterHarness;

namespace PropertyGpsApi.Tests;

/// <summary>
/// app/version-check runs before sign-in: anonymous, rate-limited, and the one path a phone
/// still on 1.0.0 (which cannot encrypt) may call with a plain body in Required mode.
/// </summary>
public class VersionCheckEndpointTests
{
    private const string VersionCheckPath = "/v1/api/gbagps/singlesite/app/version-check";
    private const string FetchPath = "/v1/api/gbagps/singlesite/propertyinfo/fetch";

    private sealed class RecordingVersions : IAppVersionService
    {
        public (long OfficerId, int RoleId)? Seen { get; private set; }

        public Task<VersionCheckResponse> CheckAsync(VersionCheckRequest request, long officerId, int roleId, CancellationToken ct)
        {
            Seen = (officerId, roleId);
            return Task.FromResult(new VersionCheckResponse { Status = "UP_TO_DATE" });
        }
    }

    private static async Task<(long OfficerId, int RoleId)> CallAs(ClaimsPrincipal user)
    {
        var versions = new RecordingVersions();
        var controller = new AppController(versions)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };

        var result = await controller.VersionCheck(new VersionCheckRequest { Platform = "Android", Version = "1.1.0" }, default);

        Assert.IsType<OkObjectResult>(result.Result);
        return versions.Seen!.Value;
    }

    [Fact]
    public async Task Before_sign_in_the_check_runs_and_is_logged_as_officer_0_role_0()
    {
        Assert.Equal((0L, 0), await CallAs(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task Signed_in_the_check_records_the_officer_and_role_from_the_token()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(GpsClaims.UserId, "11320"), new Claim(GpsClaims.RoleId, "116")], "Bearer"));

        Assert.Equal((11320L, 116), await CallAs(user));
    }

    [Fact]
    public void The_action_is_anonymous_and_rate_limited_while_the_controller_stays_authorized()
    {
        var action = typeof(AppController).GetMethod(nameof(AppController.VersionCheck))!;

        Assert.NotNull(action.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal(RateLimitPolicies.VersionCheck, action.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.NotNull(typeof(AppController).GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void Every_anonymous_action_is_rate_limited()
    {
        var anonymous = typeof(AppController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                         || t.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
                .Select(m => (Controller: t, Action: m)))
            .ToList();

        Assert.Contains(anonymous, a => a.Action.Name == nameof(AppController.VersionCheck));
        Assert.All(anonymous, a => Assert.True(
            a.Action.GetCustomAttribute<EnableRateLimitingAttribute>() is not null,
            $"{a.Controller.Name}.{a.Action.Name} is anonymous but has no rate limit"));
    }

    [Fact]
    public void The_default_allowance_is_60_per_window()
    {
        Assert.Equal(60, new OtpOptions().VersionCheckPermitLimit);
    }

    private static RequestEncryptionOptions Shipped()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"The API's appsettings.json was not copied to {AppContext.BaseDirectory}");
        return new ConfigurationBuilder().AddJsonFile(path).Build()
            .GetSection(RequestEncryptionOptions.Section).Get<RequestEncryptionOptions>()!;
    }

    [Fact]
    public void The_shipped_settings_allow_plain_bodies_on_version_check_and_nowhere_else()
    {
        Assert.Equal([VersionCheckPath], Shipped().PlainAllowedPaths);
    }

    [Fact]
    public async Task In_Required_mode_a_plain_version_check_passes_and_a_plain_fetch_is_refused()
    {
        var options = Options(RequestEncryptionMode.Required, plainAllowedPaths: [.. Shipped().PlainAllowedPaths]);
        const string body = """{"platform":"Android","version":"1.0","deviceInfo":"Android 14"}""";

        var allowed = JsonRequest(body, path: VersionCheckPath, header: false);
        Assert.True(await RunAsync(Filter(options, new CapturingLogger<RequestBodyDecryptionFilter>()),
            Context(allowed, BodyParameter())));

        var refused = JsonRequest(body, path: FetchPath, header: false);
        var error = await Assert.ThrowsAsync<ApiException>(() =>
            RunAsync(Filter(options, new CapturingLogger<RequestBodyDecryptionFilter>()), Context(refused, BodyParameter())));
        Assert.Equal(ApiErrorCodes.BodyEncryptionRequired, error.Code);
    }
}
