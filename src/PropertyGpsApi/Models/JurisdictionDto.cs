using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

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
