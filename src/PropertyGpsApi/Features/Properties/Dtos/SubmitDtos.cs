using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Features.Properties.Dtos;

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

public sealed class SubmitSiteDetails
{
    [JsonPropertyName("siteArea")] public double? SiteArea { get; init; }
    [JsonPropertyName("eastWest")] public double? EastWest { get; init; }
    [JsonPropertyName("northSouth")] public double? NorthSouth { get; init; }

    [JsonPropertyName("isPropertyUseTypeCorrect")] public int? IsPropertyUseTypeCorrect { get; init; }
    [JsonPropertyName("propertyUseType")] public string? PropertyUseType { get; init; }
    [JsonPropertyName("propertyUseTypeId")] public int? PropertyUseTypeId { get; init; }
    [JsonPropertyName("mstPlanPropertyUseType")] public string? MstPlanPropertyUseType { get; init; }

    /// <summary>All areas are square metres.</summary>
    [JsonPropertyName("commercialArea")] public double? CommercialArea { get; init; }
    [JsonPropertyName("residentialArea")] public double? ResidentialArea { get; init; }
    [JsonPropertyName("industrialArea")] public double? IndustrialArea { get; init; }

    [JsonPropertyName("isGpsLocationCorrect")] public int? IsGpsLocationCorrect { get; init; }
    [JsonPropertyName("correctedLat")] public double? CorrectedLat { get; init; }
    [JsonPropertyName("correctedLng")] public double? CorrectedLng { get; init; }

    [JsonPropertyName("isCornerPlot")] public int? IsCornerPlot { get; init; }
    [JsonPropertyName("isDeclaredRoadFacingSidesCorrect")] public int? IsDeclaredRoadFacingSidesCorrect { get; init; }
    [JsonPropertyName("roadFacingSides")] public int? RoadFacingSides { get; init; }
    [JsonPropertyName("isRoadDetailsCorrect")] public int? IsRoadDetailsCorrect { get; init; }

    [JsonPropertyName("roadDetails")] public IReadOnlyList<SubmitRoadDetail> RoadDetails { get; init; } = [];
}

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

public sealed class SubmitVerificationResponse
{
    [JsonPropertyName("applicationId")] public string ApplicationId { get; init; } = "";
    [JsonPropertyName("epid")] public string Epid { get; init; } = "";
    [JsonPropertyName("appStatus")] public int AppStatus { get; init; }
    [JsonPropertyName("roadsStored")] public int RoadsStored { get; init; }
    [JsonPropertyName("mediaStored")] public int MediaStored { get; init; }
    [JsonPropertyName("storedUtc")] public DateTimeOffset StoredUtc { get; init; }
}

/// <summary>What the road procedure hands back per road. A rejection arrives as
/// Status = 0 in a result row, not as an exception.</summary>
internal sealed class RoadWriteRow
{
    public string? Message { get; init; }
    public bool Status { get; init; }
    public int RoadRowId { get; init; }
}

internal sealed class AppWriteRow
{
    public string? Message { get; init; }
    public bool Status { get; init; }
    public int AppId { get; init; }
}

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
}
