using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

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

