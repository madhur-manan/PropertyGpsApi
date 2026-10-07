using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Which OTP senders put the code in the HTTP reply. Only DevelopmentOtpSender may: the
/// ServerLog sender exists for an internet-facing server with no SMS gateway yet, where a
/// code in the reply would let anyone who knows the URL sign in as any officer.
/// </summary>
public class OtpSenderTests
{
    private static readonly SendOtpRequest Request = new() { Mobile = "9000000001" };

    [Fact]
    public async Task ServerLog_never_returns_the_code_to_the_caller()
    {
        var response = await Service(new ServerLogOtpSender(NullLogger<ServerLogOtpSender>.Instance))
            .SendAsync(Request, default);

        Assert.Null(response.DevOtp);
        Assert.Equal(6, response.OtpLength);
    }

    [Fact]
    public async Task ServerLog_writes_the_code_to_the_log_with_the_mobile_masked()
    {
        var log = new RecordingLogger<ServerLogOtpSender>();
        var officers = new FakeOfficers();

        await Service(new ServerLogOtpSender(log), officers).SendAsync(Request, default);

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(officers.StoredOtp!, line.Message);
        Assert.Contains("0001", line.Message);
        Assert.DoesNotContain("9000000001", line.Message);
    }

    [Fact]
    public void ServerLog_logs_below_Warning_so_the_Windows_event_log_never_gets_the_code()
    {
        // The EventLog provider records Warning and above by default.
        var log = new RecordingLogger<ServerLogOtpSender>();
        new ServerLogOtpSender(log).SendAsync("9000000001", "123456", default);
        Assert.All(log.Lines, l => Assert.True(l.Level < LogLevel.Warning));
    }

    [Fact]
    public async Task Development_still_returns_the_code_for_local_testing()
    {
        var officers = new FakeOfficers();
        var response = await Service(new DevelopmentOtpSender(NullLogger<DevelopmentOtpSender>.Instance), officers)
            .SendAsync(Request, default);

        Assert.Equal(officers.StoredOtp, response.DevOtp);
    }

    [Theory]
    [InlineData("999999")]
    [InlineData("673489")]
    [InlineData(" 999999 ")]
    public async Task The_procedures_fixed_test_codes_are_refused_on_a_real_server(string code)
    {
        // FakeOfficers.ValidateOtpAsync throws NotSupportedException, so getting the API's own
        // OTP_INVALID proves the code was refused before the database was ever asked - and so
        // never counted toward the officer's lockout.
        var service = Service(new ServerLogOtpSender(NullLogger<ServerLogOtpSender>.Instance));

        var ex = await Assert.ThrowsAsync<PropertyGpsApi.Common.ApiException>(() =>
            service.VerifyAsync(new VerifyOtpRequest { Mobile = "9000000001", Otp = code }, null, default));

        Assert.Equal("OTP_INVALID", ex.Code);
    }

    [Fact]
    public async Task Development_still_lets_the_fixed_codes_through_to_the_database()
    {
        var service = Service(new DevelopmentOtpSender(NullLogger<DevelopmentOtpSender>.Instance));

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.VerifyAsync(new VerifyOtpRequest { Mobile = "9000000001", Otp = "999999" }, null, default));
    }

    [Fact]
    public async Task A_genuine_code_is_never_one_of_the_fixed_codes()
    {
        var officers = new FakeOfficers();
        var service = Service(new ServerLogOtpSender(NullLogger<ServerLogOtpSender>.Instance), officers);
        for (var i = 0; i < 200; i++)
        {
            await service.SendAsync(Request, default);
            Assert.DoesNotContain(officers.StoredOtp, OtpService.LegacyFixedOtps);
            Assert.Equal(6, officers.StoredOtp!.Length);
        }
    }

    [Theory]
    [InlineData("9000000001", "0001")]
    [InlineData("123", "***")]
    public void Mask_keeps_only_the_last_four_digits(string mobile, string expected) =>
        Assert.Equal(expected, ServerLogOtpSender.MaskMobile(mobile));

    private static OtpService Service(IOtpSender sender, FakeOfficers? officers = null) => new(
        officers ?? new FakeOfficers(),
        sender,
        new NoTokens(),
        new NoSessions(),
        Options.Create(new OtpOptions { Source = "PropertyGPS", Length = 6 }),
        Options.Create(new AuthOptions()),
        NullLogger<OtpService>.Instance);

    private sealed class FakeOfficers : IOfficerService
    {
        public string? StoredOtp { get; private set; }

        public Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct) => Task.FromResult(true);

        public Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct)
        {
            StoredOtp = otp;
            return Task.FromResult(1L);
        }

        public Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct) => throw new NotSupportedException();
        public Task<Officer?> LoadAsync(string mobile, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Jurisdiction>> MappedWardsAsync(long officerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoTokens : IJwtTokenService
    {
        public (string Token, DateTimeOffset ExpiresAt) Issue(Officer officer, Guid sessionId) => throw new NotSupportedException();
    }

    private sealed class NoSessions : IOfficerSessionStore
    {
        public Task<bool> TryStartAsync(long officerId, Guid sessionId, string? deviceId, string? clientIp, bool enforce, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsCurrentAsync(long officerId, Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
        public Task EndAsync(long officerId, Guid sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
    }
}
