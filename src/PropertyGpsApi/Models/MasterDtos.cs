using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

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
/// One road of a ward, for the road pickers on the survey form.
///
/// The JSON names are kept for the app, but the values are KSRSAC roads: <c>streetId</c> is
/// MstRoadKSRAC.Road_ID - the id space of every citizen Rd_RoadId and of the KSRSAC check on
/// submit - and <c>streetName</c> is that road's Road_Name, exactly as the submit must send
/// it back (roadId / roadName) for the road to verify. They used to be BBMP street ids
/// (Road_KSRACId), which match no Road_ID (defect D2).
/// </summary>
public sealed class StreetDto
{
    [JsonPropertyName("streetId")] public int StreetId { get; init; }
    [JsonPropertyName("streetName")] public string? StreetName { get; init; }
    [JsonPropertyName("wardId")] public int WardId { get; init; }
    [JsonPropertyName("zoneId")] public int ZoneId { get; init; }
}

/// <summary>One MstRoadKSRAC road of a ward (MasterService.StreetsAsync).</summary>
internal sealed class KsracRoadRow
{
    public int RoadId { get; init; }
    public string? RoadName { get; init; }
}
