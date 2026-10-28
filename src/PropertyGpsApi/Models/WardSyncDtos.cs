using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// One round of the ward sync: the phone says what it holds, the server answers with the
/// next batch it lacks. Repeated until MissingCount is 0.
///
/// The phone is identified by what it holds (App_Ids), never by IsPushedToGps - the flag is
/// a record written by the acknowledgement, not a filter.
/// </summary>
public sealed class WardSyncRequest
{
    /// <summary>The ward is named by its ULB code and ward number (BtoA_EPIDMetaData.MD_ULBCode + MD_WardId).</summary>
    [Range(1, int.MaxValue)]
    [JsonPropertyName("ulbCode")]
    public int UlbCode { get; init; }

    /// <summary>Text, 1 to 20 characters once trimmed.</summary>
    [WardNumber]
    [JsonPropertyName("wardNumber")]
    public string WardNumber { get; init; } = "";

    // Older builds sent these. Accepted and ignored: Property GPS works by ULB + ward number.
    [JsonPropertyName("corporationId")] public int? CorporationId { get; init; }
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }

    /// <summary>How many applications the phone holds for this ward. Logged beside the server's count.</summary>
    [Range(0, WardSyncLimits.MaxHeld)]
    [JsonPropertyName("localCount")]
    public int LocalCount { get; init; }

    /// <summary>Every App_Id the phone holds for this ward.</summary>
    [MaxLength(WardSyncLimits.MaxHeld, ErrorMessage = "Too many application ids in one request.")]
    [JsonPropertyName("knownAppIds")]
    public IReadOnlyList<int> KnownAppIds { get; init; } = [];

    /// <summary>
    /// Of those, the ones the phone already shows as returned from QC (status 400). An
    /// application QC sends back is re-sent once to a phone that holds it, so the phone
    /// learns it was returned; listing it here stops it being sent again.
    /// </summary>
    [MaxLength(WardSyncLimits.MaxHeld, ErrorMessage = "Too many application ids in one request.")]
    [JsonPropertyName("returnedAppIds")]
    public IReadOnlyList<int> ReturnedAppIds { get; init; } = [];

    /// <summary>
    /// Of those, the ones the phone holds in an outdated shape and wants replaced - today,
    /// applications downloaded before each road row's own declaration was sent, which the
    /// officer has not started. Re-sent while still workable, and still counted as held, so
    /// one that has closed is reported closed. Older phones never send it.
    /// </summary>
    [MaxLength(WardSyncLimits.MaxHeld, ErrorMessage = "Too many application ids in one request.")]
    [JsonPropertyName("refreshAppIds")]
    public IReadOnlyList<int> RefreshAppIds { get; init; } = [];

    [Range(1, WardSyncLimits.MaxBatch)]
    [JsonPropertyName("batchSize")]
    public int BatchSize { get; init; } = WardSyncLimits.MaxBatch;

    /// <summary>
    /// Ask for the held applications that are no longer workable (submitted, decided,
    /// deactivated). The phone asks once per sync, on the first round.
    /// </summary>
    [JsonPropertyName("includeClosed")]
    public bool IncludeClosed { get; init; }

    /// <summary>
    /// True (the default, what app 1.2.0 gets by not sending it): only the newest workable
    /// application per EPID, because those phones keep one record per EPID. App 1.3.0 keeps one
    /// record per App_Id and sends false: every workable application (needs db/44).
    /// </summary>
    [JsonPropertyName("onePerEpid")]
    public bool OnePerEpid { get; init; } = true;
}

public sealed class WardSyncResponse
{
    /// <summary>Workable applications in the ward on the server.</summary>
    [JsonPropertyName("serverCount")] public int ServerCount { get; init; }

    /// <summary>The phone's count, echoed.</summary>
    [JsonPropertyName("localCount")] public int LocalCount { get; init; }

    /// <summary>
    /// Survey__MinMixedExtentPercent: each extent of a Mixed plot must be at least this share
    /// of the site, in percent. Sent with every sync so the phone follows a change on the
    /// server without a new build. Set by the controller from SurveyOptions.
    /// </summary>
    [JsonPropertyName("minMixedExtentPercent")] public double MinMixedExtentPercent { get; set; }

    /// <summary>How many the phone lacks before this batch is applied.</summary>
    [JsonPropertyName("missingCount")] public int MissingCount { get; init; }

    [JsonPropertyName("returned")] public int Returned => Items.Count;

    [JsonPropertyName("items")] public IReadOnlyList<PropertyDto> Items { get; init; } = [];

    /// <summary>Held applications that are no longer workable here. Empty unless asked for.</summary>
    [JsonPropertyName("closed")] public IReadOnlyList<WardSyncClosedDto> Closed { get; init; } = [];

    /// <summary>
    /// Who holds each application the phone already has and that is still workable, 0 for
    /// nobody - sent with <see cref="Closed"/>, once per sync. An application is downloaded
    /// once, so without this an allotment or a release made on another phone never reached
    /// this one: a released record stayed "allotted to another officer" here for good.
    /// </summary>
    [JsonPropertyName("holders")] public IReadOnlyList<WardSyncHolderDto> Holders { get; init; } = [];
}

/// <summary>
/// The pull-down on the property list: who holds each of these applications now. Nothing
/// is downloaded - an allotment or a release made on another phone is all it learns.
/// </summary>
public sealed class WardHoldersRequest
{
    [Range(1, int.MaxValue)]
    [JsonPropertyName("ulbCode")]
    public int UlbCode { get; init; }

    [WardNumber]
    [JsonPropertyName("wardNumber")]
    public string WardNumber { get; init; } = "";

    // Older builds sent these. Accepted and ignored.
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }

    /// <summary>The App_Ids the phone holds for this ward.</summary>
    [MaxLength(WardSyncLimits.MaxHeld, ErrorMessage = "Too many application ids in one request.")]
    [JsonPropertyName("appIds")]
    public IReadOnlyList<int> AppIds { get; init; } = [];
}

public sealed class WardHoldersResponse
{
    /// <summary>One entry per asked-for application that is in the ward; 0 when it is free.</summary>
    [JsonPropertyName("holders")] public IReadOnlyList<WardSyncHolderDto> Holders { get; init; } = [];
}

/// <summary>
/// A survey QC returned (N2): the earlier answers, for any RI of the ward to read, and for
/// whoever allots it to start from. Read only.
/// </summary>
public sealed class ReturnedSurveyRequest
{
    [Range(1, int.MaxValue)]
    [JsonPropertyName("ulbCode")]
    public int UlbCode { get; init; }

    [WardNumber]
    [JsonPropertyName("wardNumber")]
    public string WardNumber { get; init; } = "";

    // Older builds sent these. Accepted and ignored.
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }

    [Range(1, int.MaxValue)]
    [JsonPropertyName("appId")]
    public int AppId { get; init; }
}

public sealed class ReturnedSurveyResponse
{
    [JsonPropertyName("appId")] public int AppId { get; init; }
    [JsonPropertyName("applicationId")] public string? ApplicationId { get; init; }
    [JsonPropertyName("qcRemarks")] public string? QcRemarks { get; init; }

    /// <summary>The officer whose survey QC returned.</summary>
    [JsonPropertyName("submittedBy")] public long? SubmittedBy { get; init; }
    [JsonPropertyName("submittedOn")] public DateTimeOffset? SubmittedOn { get; init; }

    /// <summary>
    /// The earlier answers, rebuilt from the survey's own rows (EarlierSurveyReader), with the
    /// names the phone sends them under. Null when no RI survey is on the server.
    /// </summary>
    [JsonPropertyName("survey")] public EarlierSurveyDto? Survey { get; init; }
}

/// <summary>
/// A submitted survey's answers, as the phone sent them (the SubmitVerificationRequest names),
/// without photos, GPS points or the dates of the visit - those are taken again.
/// </summary>
public sealed class EarlierSurveyDto
{
    [JsonPropertyName("buildingExists")] public int? BuildingExists { get; init; }
    [JsonPropertyName("propertyLandExists")] public int? PropertyLandExists { get; init; }
    [JsonPropertyName("isLandUntraceable")] public int? IsLandUntraceable { get; init; }
    [JsonPropertyName("isLandLocationUpdated")] public int? IsLandLocationUpdated { get; init; }
    [JsonPropertyName("isGovtProperty")] public int? IsGovtProperty { get; init; }
    [JsonPropertyName("govtPropertyDetails")] public string? GovtPropertyDetails { get; init; }
    [JsonPropertyName("khataRecommendation")] public string? KhataRecommendation { get; init; }
    [JsonPropertyName("khataComments")] public string? KhataComments { get; init; }
    [JsonPropertyName("remark")] public string? Remark { get; init; }

    /// <summary>Metres, as filed.</summary>
    [JsonPropertyName("actualRoadWidth")] public double? ActualRoadWidth { get; init; }

    /// <summary>
    /// Some of the earlier roads could not be tied to the application's declared roads (a
    /// survey the legacy app filed): those are left out, and the officer answers them again.
    /// </summary>
    [JsonPropertyName("roadsIncomplete")] public bool RoadsIncomplete { get; init; }

    [JsonPropertyName("siteDetails")] public SubmitSiteDetails SiteDetails { get; init; } = new();
}

public sealed class WardSyncHolderDto
{
    [JsonPropertyName("appId")] public int AppId { get; init; }

    /// <summary>The officer holding it now; 0 when it is free.</summary>
    [JsonPropertyName("assignedToUserId")] public int AssignedToUserId { get; init; }
}

public sealed class WardSyncClosedDto
{
    [JsonPropertyName("appId")] public int AppId { get; init; }

    /// <summary>The application's App_Status now; null when it is no longer active.</summary>
    [JsonPropertyName("appStatus")] public int? AppStatus { get; init; }
}

/// <summary>The phone has saved these applications - record it on the server.</summary>
public sealed class WardSyncAckRequest
{
    [Range(1, int.MaxValue)]
    [JsonPropertyName("ulbCode")]
    public int UlbCode { get; init; }

    [WardNumber]
    [JsonPropertyName("wardNumber")]
    public string WardNumber { get; init; } = "";

    // Older builds sent these. Accepted and ignored.
    [JsonPropertyName("zoneId")] public int? ZoneId { get; init; }
    [JsonPropertyName("wardId")] public int? WardId { get; init; }

    [Required]
    [MinLength(1, ErrorMessage = "Acknowledge at least one application.")]
    [MaxLength(WardSyncLimits.MaxBatch, ErrorMessage = "Acknowledge at most one batch (100) per request.")]
    [JsonPropertyName("appIds")]
    public IReadOnlyList<int> AppIds { get; init; } = [];
}

public sealed class WardSyncAckResponse
{
    [JsonPropertyName("requested")] public int Requested { get; init; }

    /// <summary>Rows whose flag or date actually changed; the rest were already recorded.</summary>
    [JsonPropertyName("updated")] public int Updated { get; init; }
}

public static class WardSyncLimits
{
    public const int MaxBatch = 100;

    /// <summary>The largest ward holds about 2,500 workable applications; this leaves room.</summary>
    public const int MaxHeld = 20000;
}

/// <summary>Result set 1 of USP_S_GpsWardSync.</summary>
internal sealed class WardSyncSummaryRow
{
    public int ServerCount { get; init; }
    public int LocalCount { get; init; }
    public int MissingCount { get; init; }
}

/// <summary>Result set 3 of USP_S_GpsWardSync.</summary>
internal sealed class WardSyncClosedRow
{
    public int AppId { get; init; }
    public int? AppStatus { get; init; }
}

/// <summary>The row USP_U_GpsWardSyncAck returns.</summary>
internal sealed class WardSyncAckRow
{
    public int Requested { get; init; }
    public int Updated { get; init; }
}
