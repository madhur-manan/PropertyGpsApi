using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// A completed survey for one property.
///
/// Every yes/no answer is int? rather than bool?, deliberately, and this is load-bearing
/// in two directions: null means the officer did not answer, which is different from 0;
/// and isGovtProperty has a third value (2 = refer to surveyor) that a bool cannot hold.
/// The serializer never omits nulls, so "not answered" travels as an explicit null rather
/// than a missing key.
/// </summary>
public sealed class SubmitVerificationRequest
{
    /// <summary>B2A application display id - what every one of the stored procedures keys
    /// on. Returned by fetch as appDisplayId.</summary>
    [Required(AllowEmptyStrings = false)]
    [JsonPropertyName("applicationId")] public string ApplicationId { get; init; } = "";

    [Required(AllowEmptyStrings = false)]
    [JsonPropertyName("epid")] public string Epid { get; init; } = "";

    [JsonPropertyName("sasId")] public string? SasId { get; init; }
    [JsonPropertyName("zoneId")] public int ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int WardId { get; init; }
    [JsonPropertyName("singleSiteId")] public int? SingleSiteId { get; init; }
    [JsonPropertyName("applicationType")] public string? ApplicationType { get; init; }

    [JsonPropertyName("buildingExists")] public int? BuildingExists { get; init; }
    [JsonPropertyName("propertyLandExists")] public int? PropertyLandExists { get; init; }
    [JsonPropertyName("isLandUntraceable")] public int? IsLandUntraceable { get; init; }
    [JsonPropertyName("isLandLocationUpdated")] public int? IsLandLocationUpdated { get; init; }

    /// <summary>0 no, 1 yes, 2 refer to surveyor, null not answered.</summary>
    [Range(0, 2)]
    [JsonPropertyName("isGovtProperty")] public int? IsGovtProperty { get; init; }

    /// <summary>The officer's written justification when they assert encroachment on
    /// government land. The highest-consequence free text in the survey, which is why it
    /// is required whenever the answer is yes.</summary>
    [MaxLength(1000)]
    [JsonPropertyName("govtPropertyDetails")] public string? GovtPropertyDetails { get; init; }

    [JsonPropertyName("isAllBhoomiSurveyNosCorrect")] public int? IsAllBhoomiSurveyNosCorrect { get; init; }
    [JsonPropertyName("matchedSurveyNo")] public int? MatchedSurveyNo { get; init; }
    [JsonPropertyName("surveyRemark")] public string? SurveyRemark { get; init; }

    [JsonPropertyName("khataRecommendation")] public string? KhataRecommendation { get; init; }
    [JsonPropertyName("khataComments")] public string? KhataComments { get; init; }

    /// <summary>Single remark field. The legacy payload carried the same text twice, as
    /// comments and remark, which invites the two to drift apart.</summary>
    [JsonPropertyName("remark")] public string? Remark { get; init; }

    /// <summary>Metres, two decimals. Always metres - never square metres, despite the
    /// destination column being named ...InSqmt.</summary>
    [JsonPropertyName("actualRoadWidth")] public double? ActualRoadWidth { get; init; }

    /// <summary>Set once on the device when the officer completes the survey, and kept
    /// across retries - which is what lets it form part of an idempotency key.</summary>
    [Required]
    [JsonPropertyName("verifiedDate")] public DateTimeOffset VerifiedDate { get; init; }

    [JsonPropertyName("nearestPublicRoadLat")] public double? NearestPublicRoadLat { get; init; }
    [JsonPropertyName("nearestPublicRoadLng")] public double? NearestPublicRoadLng { get; init; }

    // File slots. Each holds the basename of a part sent as "files"; the server names
    // whatever it actually stores.
    [JsonPropertyName("propertyImage")] public string? PropertyImage { get; init; }
    [JsonPropertyName("mapImage")] public string? MapImage { get; init; }
    [JsonPropertyName("noteSheetFile")] public string? NoteSheetFile { get; init; }

    [Required]
    [JsonPropertyName("siteDetails")] public SubmitSiteDetails SiteDetails { get; init; } = new();
}

