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
/// Placeholder for the real gateway. Deliberately fails loudly rather than silently
/// succeeding: an OTP the officer never receives must not look like a successful send.
/// </summary>
internal sealed class SmsGatewayOtpSender(ILogger<SmsGatewayOtpSender> logger) : IOtpSender
{
    public Task SendAsync(string mobile, string otp, CancellationToken ct)
    {
        logger.LogError("SMS gateway is not configured; cannot deliver an OTP to {Mobile}", mobile);
        throw new NotImplementedException(
            "The BBMP SMS gateway is not wired up yet. Provide the provider, endpoint, "
            + "credentials and DLT template code, then implement SmsGatewayOtpSender.");
    }
}
