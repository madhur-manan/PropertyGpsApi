using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.Properties.Dtos;

/// <summary>
/// The officer (cby) and role (crole) are taken from the bearer token, never from the body -
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

public sealed class ApplicationDto
{
    [JsonPropertyName("appId")] public long AppId { get; init; }
    [JsonPropertyName("applicationId")] public string? ApplicationId { get; init; }
    [JsonPropertyName("epid")] public string? Epid { get; init; }
    [JsonPropertyName("sasId")] public string? SasId { get; init; }
    [JsonPropertyName("ownerNames")] public string? OwnerNames { get; init; }
    [JsonPropertyName("dateOfApplied")] public DateTimeOffset? DateOfApplied { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("applicationType")] public string? ApplicationType { get; init; }
    [JsonPropertyName("roadType")] public string? RoadType { get; init; }

    // 0/1 ints rather than bools, matching how every other flag on this contract is carried.
    [JsonPropertyName("isObjected")] public int? IsObjected { get; init; }
    [JsonPropertyName("hasGuidanceValue")] public int? HasGuidanceValue { get; init; }
    [JsonPropertyName("queueNo")] public int? QueueNo { get; init; }
}

/// <summary>One row of USP_S_BtoA_GpsDashData_v1 at Level 5.</summary>
internal sealed class ApplicationRow
{
    public long App_ID { get; init; }
    public string? ApplicationDisplayId { get; init; }
    public string? OwnerNames { get; init; }
    public DateTime? SubmittedOn { get; init; }
    public string? PropertyId { get; init; }
    public string? SasNo { get; init; }
    public string? Status { get; init; }
    public string? Application_Type { get; init; }
    public string? RoadType { get; init; }
    public int? isObjectedFlag { get; init; }
    public int? GVFlag { get; init; }
    public int? QueueNo { get; init; }
}
