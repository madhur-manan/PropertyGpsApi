using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class SubmitVerificationResponse
{
    [JsonPropertyName("applicationId")] public string ApplicationId { get; init; } = "";
    [JsonPropertyName("epid")] public string Epid { get; init; } = "";
    [JsonPropertyName("appStatus")] public int AppStatus { get; init; }
    [JsonPropertyName("roadsStored")] public int RoadsStored { get; init; }
    [JsonPropertyName("mediaStored")] public int MediaStored { get; init; }
    [JsonPropertyName("storedUtc")] public DateTimeOffset StoredUtc { get; init; }
}

