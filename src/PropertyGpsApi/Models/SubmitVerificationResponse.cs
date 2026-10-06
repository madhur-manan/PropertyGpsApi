using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

using PropertyGpsApi.Common;

namespace PropertyGpsApi.Models;

public sealed class SubmitVerificationResponse
{
    [JsonPropertyName("applicationId")] public string ApplicationId { get; init; } = "";
    [JsonPropertyName("epid")] public string Epid { get; init; } = "";
    [JsonPropertyName("appStatus")] public int AppStatus { get; init; }
    [JsonPropertyName("roadsStored")] public int RoadsStored { get; init; }

    /// <summary>
    /// How many of the stored roads the KSRSAC master did not recognise.
    /// </summary>
    /// <remarks>
    /// These are saved, not refused - the survey succeeded. A non-zero count means QC will
    /// see a road that does not resolve to a master entry, which is the intended outcome
    /// when an officer answers "not in the list".
    /// </remarks>
    [JsonPropertyName("roadsUnverified")] public int RoadsUnverified { get; init; }

    [JsonPropertyName("mediaStored")] public int MediaStored { get; init; }
    [JsonPropertyName("storedUtc")] public DateTimeOffset StoredUtc { get; init; }

    /// <summary>
    /// Carried out of the service so the controller can put them in the envelope's
    /// <c>errors</c> array. Not serialised here - it would say the same thing twice on the
    /// wire. Two kinds:
    /// <list type="bullet">
    /// <item><c>ROAD_NOT_RECOGNISED</c>, field <c>siteDetails.roadDetails[i]</c> (i = the
    /// road's index in the submitted roadDetails): one per road the KSRSAC master did not
    /// recognise.</item>
    /// <item><c>DECLARED_ROAD_NOT_ANSWERED</c>, field <c>siteDetails.roadDetails</c>: one per
    /// active, non-blank declared road row the survey carried no entry for.</item>
    /// </list>
    /// </summary>
    [JsonIgnore] public IReadOnlyList<ApiError> Warnings { get; init; } = [];
}

