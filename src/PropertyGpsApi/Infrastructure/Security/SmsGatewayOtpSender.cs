using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Infrastructure.Security;

/// <summary>
/// BBMP's SMS gateway, in either of the two protocols BBMP's own applications already use
/// against it (<see cref="SmsOptions.Protocol"/>):
///
///   - Mgov, from the LayoutKhataAPI clsSendOtp helper: form-encoded POST, the password sent
///     as a SHA-1 hex digest, and a SHA-512 hex digest over username + senderId + message +
///     secretKey as the integrity key.
///   - SmsMobile, from the B2A_WEBAPI clSendSMS helper for smsmobile1.karnataka.gov.in: the
///     same fields, but the password is sent as it is and the key is the service key,
///     DES-encrypted with the gateway's fixed key and Base64url-encoded.
///
/// SHA-1, SHA-512-without-HMAC and DES are the gateways' protocols, not a choice - they are
/// what the providers validate against, so they cannot be "upgraded" unilaterally.
///
/// Two things are deliberately NOT carried over from the original helpers:
///   - LayoutKhataAPI set ServicePointManager.ServerCertificateValidationCallback to always
///     return true, which disables TLS certificate validation for the entire process, not
///     just this call. Credentials then travel over a connection nobody has authenticated.
///   - B2A_WEBAPI writes the whole request, password and key included, to its log.
/// </summary>
internal sealed partial class SmsGatewayOtpSender(
    HttpClient http,
    IOptions<SmsOptions> options,
    ILogger<SmsGatewayOtpSender> logger) : IOtpSender
{
    /// <summary>The key B2A_WEBAPI encrypts the service key with. Fixed by the gateway, not a secret of ours.</summary>
    private const string SmsMobileDesKey = "87654321";

    public async Task SendAsync(string mobile, string otp, CancellationToken ct)
    {
        var sms = options.Value;

        var message = sms.MessageTemplate
            .Replace("{#var1#}", sms.ApplicationName, StringComparison.Ordinal)
            .Replace("{#var2#}", otp, StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Post, sms.ApiUrl)
        {
            Content = sms.Protocol == SmsOptions.SmsMobile
                ? SmsMobileBody(sms, mobile, message)
                : MgovBody(sms, mobile, message)
        };

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"The SMS gateway returned HTTP {(int)response.StatusCode}.");

        if (sms.Protocol == SmsOptions.SmsMobile)
        {
            // This gateway's success reply is not documented, and B2A_WEBAPI counts any reply
            // as sent. Its reply is logged - digits masked - until its shape is known.
            logger.LogInformation("SMS gateway replied for {MobileSuffix}: {Reply}",
                Suffix(mobile), Masked(body));
        }
        else
        {
            // Mgov reports failure in the body, not the status code, so a 200 alone proves
            // nothing. Treating it as success would mean reporting "OTP sent" for a message
            // the officer never receives.
            var code = body.Split(',', 2)[0].Trim();
            if (!string.Equals(code, sms.SuccessCode, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The SMS gateway rejected the message with code '{code}'.");
        }

        // Never log the message body or the code itself.
        logger.LogInformation("OTP dispatched to {MobileSuffix} via the SMS gateway", Suffix(mobile));
    }

    private static FormUrlEncodedContent MgovBody(SmsOptions sms, string mobile, string message) => new(
        new Dictionary<string, string>
        {
            ["username"] = sms.Username,
            ["password"] = Sha1Hex(sms.Password),
            ["smsservicetype"] = sms.ServiceType,
            ["content"] = message,
            ["mobileno"] = mobile,
            ["senderid"] = sms.SenderId,
            ["key"] = Sha512Hex(sms.Username + sms.SenderId + message + sms.SecretKey),
            ["templateid"] = sms.TemplateId
        });

    /// <summary>
    /// Built exactly as B2A_WEBAPI builds it - each value Uri-escaped, in this order - since
    /// that is the request the gateway is known to accept.
    /// </summary>
    internal static StringContent SmsMobileBody(SmsOptions sms, string mobile, string message)
    {
        var fields = new (string Name, string Value)[]
        {
            ("username", sms.Username),
            ("password", sms.Password),
            ("smsservicetype", sms.ServiceType),
            ("content", message),
            ("mobileno", mobile.Trim()),
            ("senderid", sms.SenderId.Trim()),
            ("key", SmsMobileKey(sms.SecretKey).Trim()),
            ("templateid", sms.TemplateId.Trim())
        };
        var query = string.Join("&", fields.Select(f => $"{f.Name}={Uri.EscapeDataString(f.Value)}"));
        return new StringContent(query, Encoding.UTF8, "application/x-www-form-urlencoded");
    }

    /// <summary>DES-ECB, PKCS7, then Base64url without padding - B2A_WEBAPI's clSendSMS.Encrypt.</summary>
    internal static string SmsMobileKey(string serviceKey)
    {
        using var des = DES.Create();
        des.Key = Encoding.UTF8.GetBytes(SmsMobileDesKey);
        var encrypted = des.EncryptEcb(Encoding.UTF8.GetBytes(serviceKey), PaddingMode.PKCS7);
        return Convert.ToBase64String(encrypted).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Suffix(string mobile) => mobile.Length >= 4 ? mobile[^4..] : "****";

    /// <summary>Runs of four or more digits masked, so a reply that echoes the number or a code never lands in the log.</summary>
    internal static string Masked(string reply)
    {
        var text = reply.Length > 200 ? reply[..200] : reply;
        return LongDigits().Replace(text.ReplaceLineEndings(" "), m => new string('#', m.Length));
    }

    [GeneratedRegex("[0-9]{4,}")]
    private static partial Regex LongDigits();

    private static string Sha1Hex(string value) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Sha512Hex(string value) =>
        Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(value)));
}
