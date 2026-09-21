using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// The app reporting back which fetched applications it actually stored. A successful
/// acknowledgement sets IsPushedToGps on BtoAMainApp, which is how the server stops sending
/// a record again - so this is the step that makes the ward download idempotent.
/// </summary>
public sealed class PushStatusRequest
{
    [Required]
    [MinLength(1, ErrorMessage = "Report at least one application.")]
    [MaxLength(500, ErrorMessage = "Report at most 500 applications per request.")]
    [JsonPropertyName("items")]
    public IReadOnlyList<PushStatusItem> Items { get; init; } = [];
}

public sealed class PushStatusItem
{
    /// <summary>App_DisplayId, returned by fetch as "applicationId".</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(50)]
    [JsonPropertyName("applicationId")]
    public string ApplicationId { get; init; } = "";

    /// <summary>App_MotherEPID, returned by fetch as "epid".</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(50)]
    [JsonPropertyName("epid")]
    public string Epid { get; init; } = "";

    /// <summary>
    /// Whether the device stored the record. False records the failure without marking the
    /// application as pushed, so the server will offer it again.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    /// <summary>Optional detail, kept verbatim on the record or in the exception log.</summary>
    [MaxLength(2000)]
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class PushStatusResponse
{
    [JsonPropertyName("accepted")] public int Accepted { get; init; }
    [JsonPropertyName("rejected")] public int Rejected { get; init; }
    [JsonPropertyName("results")] public IReadOnlyList<PushStatusResult> Results { get; init; } = [];
}

public sealed class PushStatusResult
{
    [JsonPropertyName("applicationId")] public string ApplicationId { get; init; } = "";
    [JsonPropertyName("epid")] public string Epid { get; init; } = "";

    /// <summary>True when the server recorded the outcome, not when the survey succeeded.</summary>
    [JsonPropertyName("accepted")] public bool Accepted { get; init; }

    /// <summary>The database's own wording: "GPS details updated successfully.", "This record is already pushed.", "No matching application found."</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>The row USP_U_GpsPushedDetails returns.</summary>
internal sealed class PushStatusRow
{
    public string? App_DisplayId { get; init; }
    public int Status { get; init; }
    public string? Message { get; init; }
}
