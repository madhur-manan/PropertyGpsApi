using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Controllers;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// One phone per officer (db/29): sign-in writes a session and puts its id in the token,
/// every request checks it, and sign-out ends only its own session.
/// </summary>
public class OfficerSessionTests
{
    private static readonly Officer Officer = new() { OfficerId = 11320, RoleId = 116, Mobile = "9000000001", Name = "Test" };

    // ---- sign-in -------------------------------------------------------------

    [Fact]
    public async Task Sign_in_starts_the_session_before_the_token_and_puts_its_id_inside()
    {
        var sessions = new FakeSessions();
        var tokens = new RecordingTokens(sessions);
        var service = Service(sessions, tokens);

        var reply = await service.VerifyAsync(Verify("phone-b"), "10.0.0.5", default);

        var started = Assert.Single(sessions.Started);
        Assert.Equal(11320, started.OfficerId);
        Assert.Equal("phone-b", started.DeviceId);
        Assert.Equal("10.0.0.5", started.ClientIp);
        Assert.Equal(started.SessionId, tokens.IssuedFor);
        Assert.True(tokens.SessionExistedWhenIssued, "the token must not exist before its session");
        Assert.Equal("token", reply.Token);
        Assert.True(started.Enforce);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("PropertyGPS")] // the shared constant builds before 1.1.0 send
    public async Task A_phone_with_no_id_of_its_own_is_told_to_update(string? deviceId)
    {
        var sessions = new FakeSessions();
        var tokens = new RecordingTokens(sessions);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Service(sessions, tokens).VerifyAsync(Verify(deviceId), null, default));

        Assert.Equal(ApiErrorCodes.DeviceUnknown, ex.Code);
        Assert.Empty(sessions.Started);
        Assert.Null(tokens.IssuedFor);
    }

    [Fact]
    public async Task A_phone_other_than_the_registered_one_is_refused()
    {
        var sessions = new FakeSessions { Refuses = true };
        var tokens = new RecordingTokens(sessions);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Service(sessions, tokens).VerifyAsync(Verify("phone-b"), null, default));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, ex.StatusCode);
        Assert.Equal(ApiErrorCodes.SignedInElsewhere, ex.Code);
        Assert.True(ex.Recoverable);
        Assert.Contains("registered to another device", ex.Message);
        Assert.Null(tokens.IssuedFor);
    }

    [Fact]
    public async Task With_the_check_off_nothing_is_refused()
    {
        var sessions = new FakeSessions();
        await Service(sessions, new RecordingTokens(sessions), singleDevice: false).VerifyAsync(Verify("phone-b"), null, default);
        Assert.False(Assert.Single(sessions.Started).Enforce);
    }

    [Fact]
    public async Task No_session_means_no_token()
    {
        var sessions = new FakeSessions { StartFails = true };
        var tokens = new RecordingTokens(sessions);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            Service(sessions, tokens).VerifyAsync(Verify("phone-a"), null, default));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ex.StatusCode);
        Assert.True(ex.Retryable);
        Assert.Null(tokens.IssuedFor);
    }

    [Fact]
    public async Task With_the_check_off_a_missing_session_table_does_not_block_sign_in()
    {
        var sessions = new FakeSessions { StartFails = true };
        var tokens = new RecordingTokens(sessions);

        var reply = await Service(sessions, tokens, singleDevice: false).VerifyAsync(Verify("phone-a"), null, default);

        Assert.Equal("token", reply.Token);
    }

    [Fact]
    public void The_token_carries_the_session_id()
    {
        var jwt = new JwtTokenService(
            Options.Create(new JwtOptions { Key = new string('k', 40), Issuer = "i", Audience = "a" }),
            TimeProvider.System);
        var sid = Guid.NewGuid();

        var (token, _) = jwt.Issue(Officer, sid);

        var claims = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims;
        Assert.Equal(sid.ToString("D"), claims.Single(c => c.Type == GpsClaims.SessionId).Value);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" phone-a ", "phone-a")]
    public void The_install_id_is_trimmed_and_blank_is_null(string? sent, string? stored) =>
        Assert.Equal(stored, OfficerSessionStore.CleanDeviceId(sent));

    [Fact]
    public void An_overlong_install_id_is_cut_not_refused() =>
        Assert.Equal(OfficerSessionStore.MaxDeviceIdLength, OfficerSessionStore.CleanDeviceId(new string('x', 200))!.Length);

    // ---- every request -------------------------------------------------------

    [Fact]
    public async Task The_current_session_passes()
    {
        var sid = Guid.NewGuid();
        var sessions = new FakeSessions { CurrentSession = sid };
        Assert.Equal(SessionCheck.Current, await Validator(sessions).CheckAsync(User(sid), default));
    }

    [Fact]
    public async Task A_phone_replaced_by_a_newer_sign_in_is_refused()
    {
        var sessions = new FakeSessions { CurrentSession = Guid.NewGuid() };
        Assert.Equal(SessionCheck.Replaced, await Validator(sessions).CheckAsync(User(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task A_token_from_before_sessions_is_refused_as_expired()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, "11320")], "Bearer"));
        Assert.Equal(SessionCheck.NotTracked, await Validator(new FakeSessions()).CheckAsync(user, default));
    }

    [Fact]
    public async Task A_database_hiccup_does_not_sign_anyone_out()
    {
        var sessions = new FakeSessions { CheckFails = true };
        Assert.Equal(SessionCheck.Unavailable, await Validator(sessions).CheckAsync(User(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task The_switch_turns_the_check_off()
    {
        var sessions = new FakeSessions { CurrentSession = Guid.NewGuid() };
        var validator = Validator(sessions, singleDevice: false);
        Assert.Equal(SessionCheck.Current, await validator.CheckAsync(User(Guid.NewGuid()), default));
        Assert.Equal(0, sessions.Checks);
    }

    // ---- what the phone is told ---------------------------------------------

    [Fact]
    public async Task A_released_session_is_signed_out_and_told_why()
    {
        var (status, body) = await Challenge(SessionCheck.Replaced);

        Assert.Equal(401, status);
        // UNAUTHENTICATED, so every installed app signs out; errors[0] says why.
        Assert.Equal(ApiErrorCodes.AuthRequired, body.GetProperty("code").GetString());
        Assert.Equal(ApiErrorCodes.SessionReplaced, body.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Contains("session was ended", body.GetProperty("message").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task An_unavailable_check_is_retryable_and_not_a_sign_out()
    {
        var (status, body) = await Challenge(SessionCheck.Unavailable);
        Assert.Equal(503, status);
        Assert.Equal(ApiErrorCodes.Upstream, body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task Any_other_refusal_is_the_usual_expiry()
    {
        var (status, body) = await Challenge(null);
        Assert.Equal(401, status);
        Assert.Equal(ApiErrorCodes.AuthRequired, body.GetProperty("code").GetString());
        Assert.Equal(0, body.GetProperty("errors").GetArrayLength());
    }

    // ---- sign-out ------------------------------------------------------------

    [Fact]
    public async Task Sign_out_ends_this_tokens_own_session()
    {
        var sid = Guid.NewGuid();
        var sessions = new FakeSessions();
        var controller = new AuthController(null!, null!, sessions)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(sid) } }
        };

        await controller.Logout(default);

        Assert.Equal((11320L, sid), Assert.Single(sessions.Ended));
    }

    [Fact]
    public void Sign_out_needs_a_token()
    {
        var logout = typeof(AuthController).GetMethod(nameof(AuthController.Logout))!;
        Assert.Null(logout.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    // ---- helpers -------------------------------------------------------------

    private static VerifyOtpRequest Verify(string? deviceId) => new() { Mobile = "9000000001", Otp = "482913", DeviceId = deviceId };

    private static ClaimsPrincipal User(Guid sid) => new(new ClaimsIdentity(
    [
        new Claim(JwtRegisteredClaimNames.Sub, "11320"),
        new Claim(GpsClaims.UserId, "11320"),
        new Claim(GpsClaims.SessionId, sid.ToString("D"))
    ], "Bearer"));

    private static SessionValidator Validator(FakeSessions sessions, bool singleDevice = true) => new(
        sessions, Options.Create(new AuthOptions { SingleDeviceSessions = singleDevice }),
        NullLogger<SessionValidator>.Instance);

    private static async Task<(int Status, JsonElement Body)> Challenge(SessionCheck? outcome)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        if (outcome is { } o) http.Items[SessionValidator.OutcomeKey] = o;

        await SessionValidator.WriteChallengeAsync(http);

        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, JsonDocument.Parse(http.Response.Body).RootElement.Clone());
    }

    private static OtpService Service(FakeSessions sessions, RecordingTokens tokens, bool singleDevice = true) => new(
        new SignedInOfficers(),
        new ServerLogOtpSender(NullLogger<ServerLogOtpSender>.Instance),
        tokens,
        sessions,
        Options.Create(new OtpOptions { Source = "PropertyGPS", Length = 6 }),
        Options.Create(new AuthOptions { SingleDeviceSessions = singleDevice }),
        NullLogger<OtpService>.Instance);

    private sealed class FakeSessions : IOfficerSessionStore
    {
        public List<(long OfficerId, Guid SessionId, string? DeviceId, string? ClientIp, bool Enforce)> Started { get; } = [];
        public List<(long, Guid)> Ended { get; } = [];
        public Guid? CurrentSession { get; set; }
        public bool Refuses { get; init; }
        public bool StartFails { get; init; }
        public bool CheckFails { get; init; }
        public int Checks { get; private set; }

        public Task<bool> TryStartAsync(
            long officerId, Guid sessionId, string? deviceId, string? clientIp, bool enforce, CancellationToken ct)
        {
            if (StartFails) throw new InvalidOperationException("database down");
            if (Refuses && enforce) return Task.FromResult(false);
            Started.Add((officerId, sessionId, deviceId, clientIp, enforce));
            CurrentSession = sessionId;
            return Task.FromResult(true);
        }

        public Task<bool> IsCurrentAsync(long officerId, Guid sessionId, CancellationToken ct)
        {
            Checks++;
            if (CheckFails) throw new InvalidOperationException("database down");
            return Task.FromResult(CurrentSession == sessionId);
        }

        public Task EndAsync(long officerId, Guid sessionId, CancellationToken ct)
        {
            Ended.Add((officerId, sessionId));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTokens(FakeSessions sessions) : IJwtTokenService
    {
        public Guid? IssuedFor { get; private set; }
        public bool SessionExistedWhenIssued { get; private set; }

        public (string Token, DateTimeOffset ExpiresAt) Issue(Officer officer, Guid sessionId)
        {
            IssuedFor = sessionId;
            SessionExistedWhenIssued = sessions.CurrentSession == sessionId;
            return ("token", DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class SignedInOfficers : IOfficerService
    {
        public Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct) =>
            Task.FromResult(new OtpValidationResult(OtpValidationOutcome.Valid, Officer));
        public Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct) => throw new NotSupportedException();
        public Task<Officer?> LoadAsync(string mobile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Jurisdiction>> MappedWardsAsync(long officerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct) => throw new NotSupportedException();
    }
}
