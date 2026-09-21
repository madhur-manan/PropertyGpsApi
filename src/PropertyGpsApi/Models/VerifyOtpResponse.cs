using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

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

