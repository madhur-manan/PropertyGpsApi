using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>No longer returned (there are no zones in UDD_MASTERDB); kept for reference.</summary>
public sealed class ZoneDto
{
    [JsonPropertyName("zoneId")] public int ZoneId { get; init; }
    [JsonPropertyName("zoneName")] public string? ZoneName { get; init; }
}

/// <summary>One ULB of mst_AROMapping (masters/zones, which now lists ULBs).</summary>
public sealed class UlbDto
{
    [JsonPropertyName("ulbCode")] public int UlbCode { get; init; }
    [JsonPropertyName("ulbName")] public string? UlbName { get; init; }
}

/// <summary>One ward of a ULB (masters/wards), by its number.</summary>
public sealed class WardDto
{
    [JsonPropertyName("ulbCode")] public int UlbCode { get; init; }
    [JsonPropertyName("wardNumber")] public string? WardNumber { get; init; }
    [JsonPropertyName("wardName")] public string? WardName { get; init; }

    // Kept for older builds; always null now that wards are named by number.
    [JsonPropertyName("wardId")] public int? WardId { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
}

/// <summary>
/// One road of a ward, for the road pickers on the survey form.
///
/// <c>streetId</c> is the road's id as UDD_MASTERDB.dbo.USP_S_GetRoadData returns it (or the
/// ward's StreetNumber from mst_AROMapping when the procedure cannot answer), and
/// <c>streetName</c> that road's name - exactly the pair the submit sends back as roadId /
/// roadName. The id is TEXT: nothing promises it is a number.
/// </summary>
public sealed class StreetDto
{
    [JsonPropertyName("streetId")] public string StreetId { get; init; } = "";
    [JsonPropertyName("streetName")] public string StreetName { get; init; } = "";
    [JsonPropertyName("ulbCode")] public int UlbCode { get; init; }
    [JsonPropertyName("wardNumber")] public string WardNumber { get; init; } = "";

    // Kept for older builds; always null now that wards are named by ULB + number.
    [JsonPropertyName("wardId")] public int? WardId { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
}

/// <summary>
/// One road of a ward (MasterService.StreetsAsync): a row of USP_S_GetRoadData, whose columns
/// are id (nvarchar) and streetName, or of the mst_AROMapping fallback, aliased the same.
/// </summary>
internal sealed class KsracRoadRow
{
    public string? Id { get; init; }
    public string? StreetName { get; init; }
}
