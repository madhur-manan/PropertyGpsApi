namespace PropertyGpsApi.Models;

/// <summary>Which ULB and ward (BtoA_EPIDMetaData.MD_ULBCode + MD_WardId) an EPID belongs to.</summary>
public sealed record MediaScope
{
    public int? UlbCode { get; init; }

    /// <summary>The ward number as text, trimmed.</summary>
    public string? WardNumber { get; init; }

    // No longer read: Property GPS works by ULB + ward number only. Always null.
    public int? ZoneId { get; init; }
    public int? WardId { get; init; }
}
