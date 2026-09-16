using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.Masters.Dtos;

public sealed class ZoneDto
{
    [JsonPropertyName("zoneId")] public int ZoneId { get; init; }
    [JsonPropertyName("zoneName")] public string? ZoneName { get; init; }
}

public sealed class WardDto
{
    [JsonPropertyName("wardId")] public int WardId { get; init; }
    [JsonPropertyName("wardName")] public string? WardName { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
}
