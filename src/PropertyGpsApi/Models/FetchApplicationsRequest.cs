using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// The officer (cby) and role (crole) are taken from the bearer token, never the body -
/// a client must not be able to name itself and read another officer's ward.
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

    /// <summary>Optional filter for a single application.</summary>
    [MaxLength(50)]
    [JsonPropertyName("epid")]
    public string? Epid { get; init; }
}
