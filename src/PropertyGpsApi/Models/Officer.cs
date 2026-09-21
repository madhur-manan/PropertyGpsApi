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
    public int? CorporationId { get; init; }
    public string? CorporationName { get; init; }
    public int? ZoneId { get; init; }
    public int? WardId { get; init; }
    public int? DivisionId { get; init; }

    /// <summary>Every zone/ward this officer may work in, from mst_AROMapping.</summary>
    public IReadOnlyList<Jurisdiction> Jurisdictions { get; init; } = [];
}

public sealed record Jurisdiction
{
    public int? GbaZoneId { get; init; }
    public string? GbaZoneName { get; init; }
    public int? ZoneId { get; init; }
    public string? ZoneName { get; init; }
    public int? WardId { get; init; }
    public string? WardName { get; init; }
    public int? CorporationId { get; init; }
    public string? CorporationName { get; init; }
}

/// <summary>
/// One row of USP_S_Officer_ValidateOTP's result set. The procedure returns officer
/// identity and one jurisdiction row together, repeated per jurisdiction.
/// </summary>
internal sealed class OfficerOtpRow
{
    public bool IsValidOtp { get; init; }
    public long Ofcr_Id { get; init; }
    public string? Ofcr_MNo { get; init; }
    public int Ofcr_RoleId { get; init; }
    public string? Ofcr_Name { get; init; }
    public int? Ofcr_ZoneId { get; init; }
    public int? Ofcr_DivisionId { get; init; }
    public int? Ofcr_WardId { get; init; }
    public string? AdditionalInfo { get; init; }
    public int? NoOfFailedAttempt { get; init; }
    public int? ZoneId { get; init; }
    public string? ZoneName { get; init; }
    public int? CorporationId { get; init; }
    public string? CorporationName { get; init; }
    public string? BBMPWardName { get; init; }
    public string? RoleName { get; init; }
}
