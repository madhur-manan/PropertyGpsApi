using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

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

