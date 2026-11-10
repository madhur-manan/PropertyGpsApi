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

    // ---- Locked after wrong OTPs: its own message; tries left on a wrong code ----

    [Fact]
    public async Task Send_to_a_locked_RI_says_the_account_is_locked()
    {
        var service = Service(Sender(), new FakeOfficers { Exists = false, Locked = true });

        var ex = await Assert.ThrowsAsync<PropertyGpsApi.Common.ApiException>(() => service.SendAsync(Request, default));

        Assert.Equal("OFFICER_LOCKED", ex.Code);
        Assert.Equal(OtpService.LockedMessage, ex.Message);
        Assert.Contains("wrong OTP was entered 4 times", ex.Message);
    }

    [Fact]
    public async Task Send_to_an_unknown_number_keeps_the_old_message()
    {
        var service = Service(Sender(), new FakeOfficers { Exists = false, Locked = false });

        var ex = await Assert.ThrowsAsync<PropertyGpsApi.Common.ApiException>(() => service.SendAsync(Request, default));

        Assert.Equal("OFFICER_NOT_REGISTERED", ex.Code);
        Assert.Contains("not registered as an officer", ex.Message);
    }

    [Fact]
    public async Task Send_to_an_active_RI_never_asks_about_a_lock()
    {
        var officers = new FakeOfficers();
        await Service(Sender(), officers).SendAsync(Request, default);
        Assert.Equal(0, officers.LockChecks);
    }

    [Fact]
    public async Task The_wrong_code_that_locks_says_the_account_is_locked()
    {
        var officers = new FakeOfficers
        {
            Locked = true,
            Validation = new OtpValidationResult(OtpValidationOutcome.UnknownOrLockedOfficer, null),
        };

        var ex = await Assert.ThrowsAsync<PropertyGpsApi.Common.ApiException>(() => Verify(officers, "123456"));

        Assert.Equal("OFFICER_LOCKED", ex.Code);
        Assert.Equal(OtpService.LockedMessage, ex.Message);
    }

    [Fact]
    public async Task Verify_for_an_unknown_number_keeps_the_old_message()
    {
        var officers = new FakeOfficers
        {
            Validation = new OtpValidationResult(OtpValidationOutcome.UnknownOrLockedOfficer, null),
        };

        var ex = await Assert.ThrowsAsync<PropertyGpsApi.Common.ApiException>(() => Verify(officers, "123456"));

        Assert.Equal("OFFICER_NOT_REGISTERED", ex.Code);
    }

    [Theory]
    [InlineData(1, "3 attempts left")]
    [InlineData(2, "2 attempts left")]
    [InlineData(3, "1 attempt left")]
    public async Task A_wrong_code_says_how_many_tries_are_left(int used, string expected)
    {
        var officers = new FakeOfficers
        {
            Validation = new OtpValidationResult(OtpValidationOutcome.InvalidCode, null, used),
        };

        var ex = await Assert.ThrowsAsync<PropertyGpsApi.Common.ApiException>(() => Verify(officers, "123456"));

        Assert.Equal("OTP_INVALID", ex.Code);
        Assert.Contains(expected + " before your account is locked", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(4)]
    public void Without_a_usable_count_the_wrong_code_message_is_the_plain_one(int? used) =>
        Assert.Equal("That code is not correct or has expired.", OtpService.WrongCodeMessage(used));

    private static ServerLogOtpSender Sender() => new(NullLogger<ServerLogOtpSender>.Instance);

    private static Task Verify(FakeOfficers officers, string otp) =>
        Service(Sender(), officers).VerifyAsync(new VerifyOtpRequest { Mobile = "9000000001", Otp = otp }, null, default);

    private sealed class FakeOfficers : IOfficerService
    {
        public string? StoredOtp { get; private set; }
        public bool Exists { get; init; } = true;
        public bool Locked { get; init; }
        public OtpValidationResult? Validation { get; init; }
        public int LockChecks { get; private set; }

        public Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct) => Task.FromResult(Exists);

        public Task<bool> IsLockedAfterWrongOtpsAsync(string mobile, CancellationToken ct)
        {
            LockChecks++;
            return Task.FromResult(Locked);
        }

        public Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct)
        {
            StoredOtp = otp;
            return Task.FromResult(1L);
        }

        public Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct) =>
            Validation is null ? throw new NotSupportedException() : Task.FromResult(Validation);
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
