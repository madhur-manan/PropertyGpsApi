using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

public sealed class SubmitRoadDetail
{
    /// <summary>
    /// Identifies the row the officer is answering about: a Rd_RoadRow_ID of THIS
    /// application, as the fetch returned it; null for a road the officer added. Anything
    /// else (0 or below, a Site_Id, another application's row) is refused.
    /// </summary>
    [JsonPropertyName("roadRowId")] public int? RoadRowId { get; init; }

    /// <summary>
    /// A KSRSAC Road_ID (the street list's streetId), as a string so it can also hold the
    /// sentinel "999", meaning the road is not in the public list. Must parse as an int
    /// when present.
    /// </summary>
    [JsonPropertyName("roadId")] public string? RoadId { get; init; }

    /// <summary>The master Road_Name of roadId. At most 250 characters.</summary>
    [JsonPropertyName("roadName")] public string? RoadName { get; init; }

    /// <summary>The name as the officer typed or confirmed it. At most 250 characters.</summary>
    [JsonPropertyName("actualRoadName")] public string? ActualRoadName { get; init; }

    /// <summary>
    /// "public" / "private" (any case). A live "private" road must carry both points, both
    /// photographs and the notice. At most 50 characters.
    /// </summary>
    [JsonPropertyName("roadType")] public string? RoadType { get; init; }

    /// <summary>
    /// The citizen's / officer's flag, passed through to Ofcr_IsPresentInPublicRoadList.
    /// 0 or 1. It is not the KSRSAC check result, which is reported per road as a warning.
    /// </summary>
    [JsonPropertyName("isPresentInPublicRoadList")] public int? IsPresentInPublicRoadList { get; init; }

    /// <summary>0 unchanged, 1 updated, 2 deleted, 3 added by the officer.</summary>
    [Range(0, 3)]
    [JsonPropertyName("roadStatus")] public int? RoadStatus { get; init; }

    /// <summary>0 or 1; null on an added road or one marked not found (then derived from roadStatus).</summary>
    [JsonPropertyName("isRoadDetailsCorrect")] public int? IsRoadDetailsCorrect { get; init; }

    [JsonPropertyName("privateRoadLat")] public double? PrivateRoadLat { get; init; }
    [JsonPropertyName("privateRoadLng")] public double? PrivateRoadLng { get; init; }
    [JsonPropertyName("nearPublicRoadLat")] public double? NearPublicRoadLat { get; init; }
    [JsonPropertyName("nearPublicRoadLng")] public double? NearPublicRoadLng { get; init; }

    [JsonPropertyName("privateRoadImage")] public string? PrivateRoadImage { get; init; }
    [JsonPropertyName("publicRoadImage")] public string? PublicRoadImage { get; init; }
    [JsonPropertyName("noticeImage")] public string? NoticeImage { get; init; }
}

