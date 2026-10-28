namespace PropertyGpsApi.Models;

/// <summary>
/// An officer as USP_S_Officer_ValidateOTP returns them. Ofcr_Id is a bigint in
/// mst_Officer, so this is a long - an int would silently truncate.
/// </summary>
public sealed record Officer
{
    public required long OfficerId { get; init; }
    public required int RoleId { get; init; }
    public string? Mobile { get; init; }
    public string? Name { get; init; }
    public string? RoleName { get; init; }

    /// <summary>mst_Officer.Ofcr_ULBId: the officer's own ULB.</summary>
    public int? UlbCode { get; init; }

    /// <summary>mst_Officer.Ofcr_WardId: the officer's own ward NUMBER, as text, trimmed.</summary>
    public string? WardNumber { get; init; }

    // Property GPS works by ULB + ward number only. UDD_MASTERDB has no zone, corporation or
    // division column on mst_Officer, so these are never filled; kept so nothing that reads
    // them breaks.
    public int? CorporationId { get; init; }
    public string? CorporationName { get; init; }
    public int? ZoneId { get; init; }
    public int? WardId { get; init; }
    public int? DivisionId { get; init; }

    /// <summary>
    /// Every ULB + ward this officer may work in: their own ward and the wards mapped to them
    /// (mst_Ofcr_WardMapping), named from mst_AROMapping. The first is the one the token carries.
    /// </summary>
    public IReadOnlyList<Jurisdiction> Jurisdictions { get; init; } = [];
}

public sealed record Jurisdiction
{
    /// <summary>The ULB code (mst_Officer.Ofcr_ULBId, mst_Ofcr_WardMapping.Map_CorpId).</summary>
    public int? UlbCode { get; init; }
    public string? UlbName { get; init; }

    /// <summary>The ward number as text, trimmed - what BtoA_EPIDMetaData.MD_WardId holds.</summary>
    public string? WardNumber { get; init; }
    public string? WardName { get; init; }

    // No longer read (zones, GBA zones and corporations are gone from the master). Always null.
    public int? GbaZoneId { get; init; }
    public string? GbaZoneName { get; init; }
    public int? ZoneId { get; init; }
    public string? ZoneName { get; init; }
    public int? WardId { get; init; }
    public int? CorporationId { get; init; }
    public string? CorporationName { get; init; }
}

/// <summary>
/// One ULB + ward of an officer as OfficerService's jurisdiction queries return it. Src 0 is
/// the officer's own ward (mst_Officer), 1 a mapped one (mst_Ofcr_WardMapping).
/// </summary>
internal sealed class JurisdictionRow
{
    public int Src { get; init; }
    public int? UlbCode { get; init; }
    public string? UlbName { get; init; }
    public string? WardNumber { get; init; }
    public string? WardName { get; init; }
}

/// <summary>
/// One row of USP_S_Officer_ValidateOTP's result set (UDD_MASTERDB): one row per active
/// officer row for the mobile, roles 116/117 first.
/// </summary>
internal sealed class OfficerOtpRow
{
    public bool IsValidOtp { get; init; }
    public long Ofcr_Id { get; init; }
    public string? Ofcr_MNo { get; init; }
    public int Ofcr_RoleId { get; init; }
    public string? Ofcr_Name { get; init; }
    public int? Ofcr_ULBId { get; init; }

    /// <summary>The ward NUMBER (nvarchar in UDD_MASTERDB), not an id.</summary>
    public string? Ofcr_WardId { get; init; }
    public string? AdditionalInfo { get; init; }
    public int? NoOfFailedAttempt { get; init; }
    public string? RoleName { get; init; }

    // The old master's procedure returned these; the UDD_MASTERDB one does not, so they stay
    // null. Nothing reads them any more.
    public int? Ofcr_ZoneId { get; init; }
    public int? Ofcr_DivisionId { get; init; }
    public int? ZoneId { get; init; }
    public string? ZoneName { get; init; }
    public int? CorporationId { get; init; }
    public string? CorporationName { get; init; }
    public string? BBMPWardName { get; init; }
}
