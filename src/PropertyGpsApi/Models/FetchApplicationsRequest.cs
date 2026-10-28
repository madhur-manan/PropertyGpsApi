using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// The officer (cby) and role (crole) are taken from the bearer token, never the body -
/// a client must not be able to name itself and read another officer's ward.
///
/// The old fetch (USP_S_GetAppDetails, builds without WARD_SYNC). The procedure still takes
/// zone and ward ids, so those stay required; the own-ward check is by ULB + ward number,
/// taken from ulbCode / wardNumber when sent and otherwise from corporationId (the ULB code,
/// as mst_Ofcr_WardMapping.Map_CorpId is) and wardId.
/// </summary>
public sealed class FetchApplicationsRequest
{
    [Range(1, int.MaxValue)]
    [JsonPropertyName("corporationId")]
    public int CorporationId { get; init; }

    [Range(1, int.MaxValue)]
    [JsonPropertyName("zoneId")]
    public int ZoneId { get; init; }

    [Range(1, int.MaxValue)]
    [JsonPropertyName("wardId")]
    public int WardId { get; init; }

    /// <summary>Optional: the ULB the own-ward check uses; corporationId when absent.</summary>
    [Range(1, int.MaxValue)]
    [JsonPropertyName("ulbCode")]
    public int? UlbCode { get; init; }

    /// <summary>Optional: the ward number the own-ward check uses; wardId when absent.</summary>
    [MaxLength(WardNumbers.MaxLength)]
    [JsonPropertyName("wardNumber")]
    public string? WardNumber { get; init; }

    /// <summary>Optional filter for a single application.</summary>
    [MaxLength(50)]
    [JsonPropertyName("epid")]
    public string? Epid { get; init; }
}
