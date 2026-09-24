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
    /// One entry per unrecognised road, carried out of the service so the controller can
    /// put it in the envelope's <c>errors</c> array. Not serialised here - it would say the
    /// same thing twice on the wire.
    /// </summary>
    [JsonIgnore] public IReadOnlyList<ApiError> Warnings { get; init; } = [];
}

