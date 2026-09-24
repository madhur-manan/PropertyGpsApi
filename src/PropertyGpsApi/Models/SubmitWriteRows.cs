using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PropertyGpsApi.Models;

/// <summary>What the road procedure hands back per road.</summary>
/// <remarks>
/// Two different things, and conflating them is what made a legitimate survey
/// unsubmittable:
/// <list type="bullet">
/// <item><c>Status</c> means STORED. False is a genuine write failure and still aborts
/// the submit, because the road is not in the database.</item>
/// <item><c>KsracMatched</c> means RECOGNISED - the road matched
/// masterDB_prod.dbo.MstRoadKSRAC on id and exact name. False is a warning: the officer's
/// answer is stored as given and QC decides.</item>
/// </list>
/// Neither arrives as an exception; both are columns in a result row.
/// </remarks>
internal sealed class RoadWriteRow
{
    public string? Message { get; init; }
    public bool Status { get; init; }
    public int RoadRowId { get; init; }
    public bool KsracMatched { get; init; }
}

internal sealed class AppWriteRow
{
    public string? Message { get; init; }
    public bool Status { get; init; }
    public int AppId { get; init; }
}

