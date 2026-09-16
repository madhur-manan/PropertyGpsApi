using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Security;

/// <summary>
/// BBMP's SMS gateway. The wire format is taken from the LayoutKhataAPI clsSendOtp helper
/// that is already in production against the same gateway: form-encoded POST, the password
/// sent as a SHA-1 hex digest, and a SHA-512 hex digest over
/// username + senderId + message + secretKey as the integrity key.
///
/// SHA-1 and SHA-512-without-HMAC are the gateway's protocol, not a choice - they are what
/// the provider validates against, so they cannot be "upgraded" unilaterally.
///
/// Two things are deliberately NOT carried over from the original helper:
///   - it set ServicePointManager.ServerCertificateValidationCallback to always return true,
///     which disables TLS certificate validation for the entire process, not just this call.
///     Credentials then travel over a connection nobody has authenticated.
///   - it used HttpWebRequest with ConnectionLimit = 1 and HTTP/1.0. A pooled HttpClient
///     from IHttpClientFactory handles connection reuse and DNS rotation properly.
/// </summary>
internal sealed class SmsGatewayOtpSender(
    HttpClient http,
    IOptions<SmsOptions> options,
    ILogger<SmsGatewayOtpSender> logger) : IOtpSender
{
    public async Task SendAsync(string mobile, string otp, CancellationToken ct)
    {
        var sms = options.Value;

        var message = sms.MessageTemplate
            .Replace("{#var1#}", sms.ApplicationName, StringComparison.Ordinal)
            .Replace("{#var2#}", otp, StringComparison.Ordinal);

        var form = new Dictionary<string, string>
        {
            ["username"] = sms.Username,
            ["password"] = Sha1Hex(sms.Password),
            ["smsservicetype"] = sms.ServiceType,
            ["content"] = message,
            ["mobileno"] = mobile,
            ["senderid"] = sms.SenderId,
            ["key"] = Sha512Hex(sms.Username + sms.SenderId + message + sms.SecretKey),
            ["templateid"] = sms.TemplateId
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, sms.ApiUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The SMS gateway returned HTTP {(int)response.StatusCode}.");

        // The gateway reports failure in the body, not the status code, so a 200 alone
        // proves nothing. Treating it as success would mean reporting "OTP sent" for a
        // message the officer never receives.
        var code = body.Split(',', 2)[0].Trim();
        if (!string.Equals(code, sms.SuccessCode, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The SMS gateway rejected the message with code '{code}'.");

        // Never log the message body or the code itself.
        logger.LogInformation("OTP dispatched to {MobileSuffix} via the SMS gateway",
            mobile.Length >= 4 ? mobile[^4..] : "****");
    }

    private static string Sha1Hex(string value) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Sha512Hex(string value) =>
        Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(value)));
}
