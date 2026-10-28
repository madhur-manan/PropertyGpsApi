using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class JurisdictionDto
{
    /// <summary>The ULB and ward number every ward request sends back (ulbCode + wardNumber).</summary>
    [JsonPropertyName("ulbCode")] public int UlbCode { get; init; }
    [JsonPropertyName("ulbName")] public string? UlbName { get; init; }
    [JsonPropertyName("wardNumber")] public string? WardNumber { get; init; }
    [JsonPropertyName("wardName")] public string? WardName { get; init; }

    // Kept for older builds, which read them; always null now that there are no zones.
    [JsonPropertyName("gbaZoneId")] public int? GbaZoneId { get; init; }
    [JsonPropertyName("gbaZoneName")] public string? GbaZoneName { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("zoneName")] public string? ZoneName { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }
    [JsonPropertyName("corporationId")] public int? CorporationId { get; init; }
    [JsonPropertyName("corporationName")] public string? CorporationName { get; init; }
}
