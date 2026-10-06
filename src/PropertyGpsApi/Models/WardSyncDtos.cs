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
    [Range(1, int.MaxValue)]
    [JsonPropertyName("corporationId")]
    public int CorporationId { get; init; }

    [Range(1, int.MaxValue)]
    [JsonPropertyName("zoneId")]
    public int ZoneId { get; init; }

    [Range(1, int.MaxValue)]
    [JsonPropertyName("wardId")]
    public int WardId { get; init; }

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
}

public sealed class WardSyncResponse
{
    /// <summary>Workable applications in the ward on the server.</summary>
    [JsonPropertyName("serverCount")] public int ServerCount { get; init; }

    /// <summary>The phone's count, echoed.</summary>
    [JsonPropertyName("localCount")] public int LocalCount { get; init; }

    /// <summary>How many the phone lacks before this batch is applied.</summary>
    [JsonPropertyName("missingCount")] public int MissingCount { get; init; }

    [JsonPropertyName("returned")] public int Returned => Items.Count;

    [JsonPropertyName("items")] public IReadOnlyList<PropertyDto> Items { get; init; } = [];

    /// <summary>Held applications that are no longer workable here. Empty unless asked for.</summary>
    [JsonPropertyName("closed")] public IReadOnlyList<WardSyncClosedDto> Closed { get; init; } = [];
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
    [JsonPropertyName("zoneId")]
    public int ZoneId { get; init; }

    [Range(1, int.MaxValue)]
    [JsonPropertyName("wardId")]
    public int WardId { get; init; }

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
