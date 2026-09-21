using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class SubmitRoadDetail
{
    /// <summary>Identifies the row the officer is answering about. Comes back from fetch.</summary>
    [JsonPropertyName("roadRowId")] public int? RoadRowId { get; init; }

    /// <summary>String, not int: it must hold both a real id and the sentinel "999",
    /// meaning the road is not in the public list.</summary>
    [JsonPropertyName("roadId")] public string? RoadId { get; init; }

    [JsonPropertyName("roadName")] public string? RoadName { get; init; }
    [JsonPropertyName("actualRoadName")] public string? ActualRoadName { get; init; }
    [JsonPropertyName("roadType")] public string? RoadType { get; init; }
    [JsonPropertyName("isPresentInPublicRoadList")] public int? IsPresentInPublicRoadList { get; init; }

    /// <summary>0 unchanged, 1 updated, 2 deleted, 3 added by the officer.</summary>
    [Range(0, 3)]
    [JsonPropertyName("roadStatus")] public int? RoadStatus { get; init; }

    [JsonPropertyName("isRoadDetailsCorrect")] public int? IsRoadDetailsCorrect { get; init; }

    [JsonPropertyName("privateRoadLat")] public double? PrivateRoadLat { get; init; }
    [JsonPropertyName("privateRoadLng")] public double? PrivateRoadLng { get; init; }
    [JsonPropertyName("nearPublicRoadLat")] public double? NearPublicRoadLat { get; init; }
    [JsonPropertyName("nearPublicRoadLng")] public double? NearPublicRoadLng { get; init; }

    [JsonPropertyName("privateRoadImage")] public string? PrivateRoadImage { get; init; }
    [JsonPropertyName("publicRoadImage")] public string? PublicRoadImage { get; init; }
    [JsonPropertyName("noticeImage")] public string? NoticeImage { get; init; }
}

