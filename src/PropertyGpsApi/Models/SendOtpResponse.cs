using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class SendOtpResponse
{
    /// <summary>
    /// The OTP_Tran row id for this challenge - an audit handle, NOT the code the officer
    /// types. It was called requestId and, being six digits like the code itself, was easy
    /// to paste into the wrong field.
    /// </summary>
    [JsonPropertyName("otpRequestId")] public string OtpRequestId { get; init; } = "";

    /// <summary>
    /// How long the client must wait before offering Resend. The Flutter app counts this
    /// down to gate its Resend button, so it is the resend window and NOT the code's
    /// validity - those are reported separately on purpose.
    /// </summary>
    [JsonPropertyName("resendAfterSeconds")] public int ResendAfterSeconds { get; init; }

    [JsonPropertyName("otpValidForSeconds")] public int OtpValidForSeconds { get; init; }

    /// <summary>Six for this backend. The app's OTP box must size itself from this.</summary>
    [JsonPropertyName("otpLength")] public int OtpLength { get; init; }

    /// <summary>
    /// The code itself, so the flow can be demonstrated and tested without an SMS.
    /// Populated ONLY when the Development OTP sender is in use, and Program.cs refuses to
    /// start with that sender outside the Development environment - so a deployed instance
    /// cannot return it. Null in every other configuration.
    /// </summary>
    [JsonPropertyName("devOtp")] public string? DevOtp { get; init; }
}

