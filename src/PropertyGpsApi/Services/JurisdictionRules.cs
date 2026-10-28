using PropertyGpsApi.Common;

using PropertyGpsApi.Models;

namespace PropertyGpsApi.Services;

/// <summary>
/// Who may see and change which property, by role.
///
/// These are the authorisation rules for this feature, and they were previously
/// copy-pasted across three places in PropertyInfoController — the ward check
/// in <c>fetch</c>, the same check again in <c>add-new</c> with different
/// wording, and a role table in <c>MayView</c>. Three copies of a security rule
/// is three chances for one of them to drift.
///
/// Deliberately a plain static class: no state, no dependencies, no interface.
/// It is called by the services rather than being one, so the rules can be read
/// in one place and unit-tested without a database or an HTTP context.
///
/// A ward is a ULB code plus a ward number (text) - Property GPS no longer knows zones,
/// corporations or divisions. The officer's wards are passed in, never read here: for a
/// ward officer they are every ward mapped to them (IOfficerService.MappedWardsAsync), not
/// the token's single ward, which refused a two-ward officer their second.
/// </summary>
internal static class JurisdictionRules
{
    /// <summary>
    /// Revenue Inspector (RI): the only role Property GPS signs in, limited to the wards mapped
    /// to them. 116 (Case Worker) is not a Property GPS role on UDD and is never ward-scoped.
    /// </summary>
    internal const int RevenueInspector = 117;

    /// <summary>
    /// Above ward level (once the zone officer). Zones are gone, so the officer's own ULB
    /// (the token's ulb claim) is what limits them, and only when they have one.
    /// </summary>
    internal const int ZoneOfficer = 125;

    /// <summary>117, the RI: the field officer whose work is ward by ward.</summary>
    internal static bool IsWardScoped(int roleId) => roleId == RevenueInspector;

    /// <summary>Whether one of these wards is this ULB + ward number (WardNumbers.Same).</summary>
    internal static bool Covers(IEnumerable<Jurisdiction>? wards, int? ulbCode, string? wardNumber) =>
        ulbCode is > 0 && wards is not null
        && wards.Any(w => w.UlbCode == ulbCode && WardNumbers.Same(w.WardNumber, wardNumber));

    /// <summary>
    /// A ward officer may only act on a ward mapped to them - any of them. Without this the
    /// token authenticates but authorises nothing, and any officer could page through every
    /// ward in the city by changing two values in the request body.
    ///
    /// A ward officer mapped to no ward at all may act on none: the ward sync refuses them
    /// the same way, so they cannot have been sent an application to act on. Other roles
    /// are not limited here.
    /// </summary>
    public static void RequireOwnWard(
        int roleId, IReadOnlyCollection<Jurisdiction>? officerWards, int? ulbCode, string? wardNumber,
        string message)
    {
        if (!IsWardScoped(roleId)) return;
        if (Covers(officerWards, ulbCode, wardNumber)) return;

        throw ApiException.Forbidden(message, ApiErrorCodes.OutsideJurisdiction);
    }

    /// <summary>
    /// Whether this officer may read a document belonging to the given ULB/ward. Any role
    /// not listed is refused outright rather than defaulting to allowed.
    /// </summary>
    public static bool MayViewMedia(
        int roleId, IReadOnlyCollection<Jurisdiction>? officerWards, long? officerUlbCode, MediaScope scope) => roleId switch
    {
        RevenueInspector => Covers(officerWards, scope.UlbCode, scope.WardNumber),
        ZoneOfficer => officerUlbCode is not { } ulb || ulb == scope.UlbCode,
        _ => false
    };
}
