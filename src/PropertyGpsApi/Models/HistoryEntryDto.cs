using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>One previously submitted survey, for the Property History screen.</summary>
public sealed class HistoryEntryDto
{
    [JsonPropertyName("applicationId")] public string? ApplicationId { get; init; }

    /// <summary>
    /// BtoAMainApp.App_Id: one EPID can carry several applications, so the phone matches a
    /// history entry to its record by this, not by the EPID. Null when the application is gone.
    /// </summary>
    [JsonPropertyName("appId")] public int? AppId { get; init; }
    [JsonPropertyName("epid")] public string? Epid { get; init; }
    [JsonPropertyName("sasId")] public string? SasId { get; init; }
    [JsonPropertyName("ulbCode")] public int? UlbCode { get; init; }

    /// <summary>The ward number as text (BtoA_EPIDMetaData.MD_WardId, trimmed).</summary>
    [JsonPropertyName("wardNumber")] public string? WardNumber { get; init; }

    // Kept for older builds; no longer read, so always null.
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }
    [JsonPropertyName("appStatus")] public int? AppStatus { get; init; }
    [JsonPropertyName("submittedOn")] public DateTimeOffset? SubmittedOn { get; init; }
    [JsonPropertyName("roadCount")] public int RoadCount { get; init; }
    [JsonPropertyName("lastStatusId")] public int? LastStatusId { get; init; }
    [JsonPropertyName("lastRemark")] public string? LastRemark { get; init; }
    [JsonPropertyName("appliedOn")] public DateTimeOffset? AppliedOn { get; init; }
    [JsonPropertyName("ownerName")] public string? OwnerName { get; init; }
    [JsonPropertyName("ownerMobile")] public string? OwnerMobile { get; init; }
    [JsonPropertyName("qcRemark")] public string? QcRemark { get; init; }
    [JsonPropertyName("qcOutcome")] public string? QcOutcome { get; init; }
    [JsonPropertyName("qcActedOn")] public DateTimeOffset? QcActedOn { get; init; }
    [JsonPropertyName("streetName")] public string? StreetName { get; init; }
}
