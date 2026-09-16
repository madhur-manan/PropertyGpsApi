using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.Auth.Dtos;

public sealed class SendOtpRequest
{
    /// <summary>Indian mobile number, ten digits starting 6-9.</summary>
    [Required]
    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Enter a valid 10-digit mobile number.")]
    [JsonPropertyName("mobile")]
    public string Mobile { get; init; } = "";

    [MaxLength(100)]
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    /// <summary>
    /// Officer role. USP_S_ValidateOfficer matches on role as well as mobile, so a wrong
    /// value reads as "not registered". Defaults to Otp:DefaultRoleId (116, the ward RI).
    /// </summary>
    [JsonPropertyName("roleId")]
    public int? RoleId { get; init; }
}

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

public sealed class VerifyOtpRequest
{
    [Required]
    [RegularExpression(@"^[6-9]\d{9}$", ErrorMessage = "Enter a valid 10-digit mobile number.")]
    [JsonPropertyName("mobile")]
    public string Mobile { get; init; } = "";

    [Required]
    [RegularExpression(@"^\d{4,8}$", ErrorMessage = "Enter the numeric code from the SMS.")]
    [JsonPropertyName("otp")]
    public string Otp { get; init; } = "";

    [JsonPropertyName("otpRequestId")] public string? OtpRequestId { get; init; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; init; }
}

public sealed class VerifyOtpResponse
{
    [JsonPropertyName("token")] public string Token { get; init; } = "";
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; init; }
    [JsonPropertyName("userId")] public long UserId { get; init; }
    [JsonPropertyName("roleId")] public int RoleId { get; init; }
    [JsonPropertyName("mobile")] public string? Mobile { get; init; }
    [JsonPropertyName("userName")] public string? UserName { get; init; }
    [JsonPropertyName("designation")] public string? Designation { get; init; }
    [JsonPropertyName("corporationId")] public int? CorporationId { get; init; }
    [JsonPropertyName("corporationName")] public string? CorporationName { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }
    [JsonPropertyName("jurisdictions")] public IReadOnlyList<JurisdictionDto> Jurisdictions { get; init; } = [];
}

public sealed class JurisdictionDto
{
    [JsonPropertyName("gbaZoneId")] public int? GbaZoneId { get; init; }
    [JsonPropertyName("gbaZoneName")] public string? GbaZoneName { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("zoneName")] public string? ZoneName { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }
    [JsonPropertyName("wardName")] public string? WardName { get; init; }
    [JsonPropertyName("corporationId")] public int? CorporationId { get; init; }
    [JsonPropertyName("corporationName")] public string? CorporationName { get; init; }
}
