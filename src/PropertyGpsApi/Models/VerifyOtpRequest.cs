using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

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

