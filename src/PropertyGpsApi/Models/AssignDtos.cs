using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>
/// "Allot to me". The officer is taken from the bearer token, never the body - the legacy
/// contract carried an assignedUserId field, which would have let any caller allot work to
/// somebody else.
/// </summary>
public sealed class AssignRequest
{
    /// <summary>The internal application id, as returned by propertyinfo/fetch as "appId".</summary>
    [Range(1, int.MaxValue)]
    [JsonPropertyName("appId")]
    public int AppId { get; init; }
}

public sealed class AssignResponse
{
    [JsonPropertyName("assignId")] public int AssignId { get; init; }
    [JsonPropertyName("appId")] public int AppId { get; init; }

    /// <summary>The database's own wording, passed through so the officer sees one message.</summary>
    [JsonPropertyName("message")] public string? Message { get; init; }
}

/// <summary>The single row USP_IU_Architect_AssignedApp returns.</summary>
internal sealed class AssignResultRow
{
    public int Assign_Id { get; init; }
    public int App_id { get; init; }
    public string? Message { get; init; }
    public int StatusCode { get; init; }
}

/// <summary>An active row of BtoA_Architect_AssignedApp, used to enrich the ward list.</summary>
internal sealed class AssignmentRow
{
    public int AppId { get; init; }
    public int Arch_Id { get; init; }
    public string? Arch_Name { get; init; }
    public string? Status { get; init; }
    public DateTime? AssignmentDate { get; init; }
}
