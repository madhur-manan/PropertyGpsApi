using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>One previously submitted survey, for the Property History screen.</summary>
public sealed class HistoryEntryDto
{
    [JsonPropertyName("applicationId")] public string? ApplicationId { get; init; }
    [JsonPropertyName("epid")] public string? Epid { get; init; }
    [JsonPropertyName("sasId")] public string? SasId { get; init; }
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
