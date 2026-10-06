namespace PropertyGpsApi.Infrastructure.Security;

/// <summary>
/// The seam for BBMP's SMS gateway. USP_I_OTP only stores the code - it does not send
/// anything - so dispatch is ours to do. The real implementation drops in here once the
/// provider, endpoint and credentials arrive, with no other change.
/// </summary>
public interface IOtpSender
{
    Task SendAsync(string mobile, string otp, CancellationToken ct);
}

/// <summary>
/// Logs the code instead of sending it. Registration is refused outside Development
/// (see Program.cs) because this writes an authentication code in clear text.
/// </summary>
internal sealed class DevelopmentOtpSender(ILogger<DevelopmentOtpSender> logger) : IOtpSender
{
    public Task SendAsync(string mobile, string otp, CancellationToken ct)
    {
        logger.LogWarning("DEV OTP for {Mobile} is {Otp} (no SMS sent)", mobile, otp);
        return Task.CompletedTask;
    }
}

/// <summary>
/// For a server that is reachable from the internet before the SMS gateway exists: the code
/// is written to the server's own log and nowhere else. Unlike DevelopmentOtpSender it never
/// reaches the HTTP reply (OtpService only fills devOtp for DevelopmentOtpSender), so having
/// the URL is not enough to sign in - only someone who can read the server's log can.
///
/// Logged at Information, not Warning, so it lands in the stdout log file only: the Windows
/// Event Log provider records Warning and above, and an authentication code has no business
/// in a log that every administrator's tooling collects. The mobile is masked to its last
/// four digits - enough to match a code to the officer who asked for it, no more.
/// </summary>
internal sealed class ServerLogOtpSender(ILogger<ServerLogOtpSender> logger) : IOtpSender
{
    public Task SendAsync(string mobile, string otp, CancellationToken ct)
    {
        logger.LogInformation("OTP for mobile ending {MobileEnd} is {Otp} (SMS not configured)",
            MaskMobile(mobile), otp);
        return Task.CompletedTask;
    }

    internal static string MaskMobile(string mobile) =>
        mobile.Length <= 4 ? new string('*', mobile.Length) : mobile[^4..];
}

