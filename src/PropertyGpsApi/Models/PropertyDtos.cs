using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// One application awaiting field verification, as USP_S_GetAppDetails returns it.
/// This is the payload the device caches in SQLite.
/// </summary>
public sealed class PropertyDto
{
    [JsonPropertyName("appId")] public int AppId { get; init; }
    [JsonPropertyName("applicationId")] public string? ApplicationId { get; init; }
    [JsonPropertyName("epid")] public string? Epid { get; init; }
    /// <summary>
    /// Every owner on the application, joined into one line. The officer's list shows this:
    /// nobody recognises a property by its EPID, they recognise it by whose it is.
    /// </summary>
    [JsonPropertyName("ownerNames")] public string? OwnerNames { get; init; }

    [JsonPropertyName("ownerNumbers")] public string? OwnerNumbers { get; init; }

    [JsonPropertyName("sasId")] public string? SasId { get; init; }
    [JsonPropertyName("applicationType")] public string? ApplicationType { get; init; }
    [JsonPropertyName("appType")] public string? AppType { get; init; }
    [JsonPropertyName("status")] public int? Status { get; init; }
    [JsonPropertyName("remarks")] public string? Remarks { get; init; }
    [JsonPropertyName("additionalInfo")] public string? AdditionalInfo { get; init; }

    [JsonPropertyName("processingFeePaid")] public int? ProcessingFeePaid { get; init; }
    [JsonPropertyName("processingFee")] public decimal? ProcessingFee { get; init; }

    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("zoneName")] public string? ZoneName { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }

    [JsonPropertyName("appliedOn")] public DateTimeOffset? AppliedOn { get; init; }

    [JsonPropertyName("khataType")] public string? KhataType { get; init; }
    [JsonPropertyName("khataLat")] public string? KhataLat { get; init; }
    [JsonPropertyName("khataLng")] public string? KhataLng { get; init; }

    /// <summary>
    /// MD_JSON, passed through untouched. It is the EPID metadata blob the legacy system
    /// stores as text; parsing it here would guess at a shape nobody has specified.
    /// </summary>
    [JsonPropertyName("epidJson")] public string? EpidJson { get; init; }

    [JsonPropertyName("siteDetails")] public SiteDetailsDto? SiteDetails { get; init; }

    // Assignment state, merged from BtoA_Architect_AssignedApp.
    [JsonPropertyName("assignedToUserId")] public int? AssignedToUserId { get; init; }
    [JsonPropertyName("assignedToName")] public string? AssignedToName { get; init; }
    [JsonPropertyName("assignmentStatus")] public string? AssignmentStatus { get; init; }
    [JsonPropertyName("assignedOn")] public DateTimeOffset? AssignedOn { get; init; }
    [JsonPropertyName("isAssignedToMe")] public bool IsAssignedToMe { get; init; }
    [JsonPropertyName("isAssignedToOther")] public bool IsAssignedToOther { get; init; }
}

public sealed class SiteDetailsDto
{
    [JsonPropertyName("siteId")] public int? SiteId { get; init; }
    [JsonPropertyName("streetId")] public int? StreetId { get; init; }
    [JsonPropertyName("roadType")] public string? RoadType { get; init; }
    [JsonPropertyName("roadId")] public string? RoadId { get; init; }
    [JsonPropertyName("roadName")] public string? RoadName { get; init; }
    [JsonPropertyName("publicRoadName")] public string? PublicRoadName { get; init; }

    [JsonPropertyName("privateRoadId")] public string? PrivateRoadId { get; init; }
    [JsonPropertyName("privateRoadName")] public string? PrivateRoadName { get; init; }
    [JsonPropertyName("privateRoadText")] public string? PrivateRoadText { get; init; }

    [JsonPropertyName("isSameLocationAsKhata")] public int? IsSameLocationAsKhata { get; init; }
    [JsonPropertyName("latitude")] public string? Latitude { get; init; }
    [JsonPropertyName("longitude")] public string? Longitude { get; init; }
    [JsonPropertyName("siteOrder")] public int? SiteOrder { get; init; }

    [JsonPropertyName("isPropertySurvey")] public int? IsPropertySurvey { get; init; }
    [JsonPropertyName("isPropertySurveyLocated")] public int? IsPropertySurveyLocated { get; init; }
    [JsonPropertyName("dcConversionType")] public string? DcConversionType { get; init; }
    [JsonPropertyName("additionalInfo")] public string? AdditionalInfo { get; init; }

    [JsonPropertyName("propertyUseType")] public string? PropertyUseType { get; init; }
    [JsonPropertyName("commercialExtentSqft")] public decimal? CommercialExtentSqft { get; init; }
    [JsonPropertyName("residentialExtentSqft")] public decimal? ResidentialExtentSqft { get; init; }

    /// <summary>0/1, and null when never recorded - not collapsed to 0.</summary>
    [JsonPropertyName("isCornerPlot")] public int? IsCornerPlot { get; init; }
    [JsonPropertyName("roadFacingSides")] public int? RoadFacingSides { get; init; }

    /// <summary>
    /// Every Rd_RoadRow_ID the procedure emitted for this application. A corner plot joins
    /// to one row per declared road, so the procedure returns several rows for it - they are
    /// collected here rather than left to duplicate the whole application.
    /// </summary>
    [JsonPropertyName("roadRowIds")] public IReadOnlyList<int> RoadRowIds { get; init; } = [];
}

/// <summary>One raw row of USP_S_GetAppDetails. Column names match its aliases exactly.</summary>
internal sealed class PropertyRow
{
    public int AppID { get; init; }
    public string? MotherEPID { get; init; }
    public string? Type { get; init; }
    public int? Status { get; init; }
    public string? Remarks { get; init; }
    public string? AdditionalInfo { get; init; }
    public string? AppDisplayId { get; init; }
    public string? ApplicationType { get; init; }
    public string? MotherSASID { get; init; }
    public int? isProcessingFeePaid { get; init; }
    public decimal? ProcessingFee { get; init; }
    public int? Site_Id { get; init; }
    public int? ApplicationId { get; init; }
    public string? EpID { get; init; }
    public int? WardId { get; init; }
    public int? ZoneId { get; init; }
    public string? GBAZoneName_En { get; init; }
    public int? StreetId { get; init; }
    public string? RoadType { get; init; }
    public string? RoadId { get; init; }
    public string? Roadname { get; init; }
    public int? IsSameLocationAsKhata { get; init; }
    public string? Latitude { get; init; }
    public string? Longitude { get; init; }
    public int? SiteOrder { get; init; }
    public int? App_IsPropertySurvey { get; init; }
    public int? App_IsPropertySurveyLocated { get; init; }
    public string? EpidJSON { get; init; }
    public string? Khatatype { get; init; }
    public string? KhataLatitude { get; init; }
    public string? KhataLongitude { get; init; }
    public DateTime? App_Cdte { get; init; }
    public string? DcConversionType { get; init; }
    public string? PrivateRoadId { get; init; }
    public string? PrivateRoadText { get; init; }
    public string? PrivateRoadName { get; init; }
    public string? Site_AdditionalInfo { get; init; }
    public string? publicRoadname { get; init; }
    public string? propertyUseType { get; init; }
    public decimal? comercialExtentinSqft { get; init; }
    public decimal? residentailsExtentinSqft { get; init; }
    public int? isCornorPlot { get; init; }
    public int? Site_numberOfRoadFacingSides { get; init; }
    public int? Rd_RoadRow_ID { get; init; }
}
