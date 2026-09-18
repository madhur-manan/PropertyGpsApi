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

/// <summary>
/// One street in a ward, for the road pickers on the survey form.
///
/// Without this list both pickers fall back to free text, which is how the same road ends
/// up spelled three ways across a ward and stops matching the KSRSAC master on submit.
/// </summary>
public sealed class StreetDto
{
    [JsonPropertyName("streetId")] public int StreetId { get; init; }
    [JsonPropertyName("streetName")] public string? StreetName { get; init; }
    [JsonPropertyName("wardId")] public int WardId { get; init; }
    [JsonPropertyName("zoneId")] public int ZoneId { get; init; }
}

/// <summary>
/// USP_S_GetMasterStreetDetails returns the same fifteen columns for all nine of its
/// levels, with the ones that do not apply cast to NULL. This is that shape; only four of
/// the columns carry anything at level 5.
/// </summary>
internal sealed class MasterStreetRow
{
    public int? CorporationID { get; init; }
    public int? ZoneID { get; init; }
    public int? WardID { get; init; }
    public int? StreetID { get; init; }
    public string? StreetName { get; init; }
}
