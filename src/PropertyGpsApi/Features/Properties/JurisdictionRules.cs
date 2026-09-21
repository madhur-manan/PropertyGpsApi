using PropertyGpsApi.Common;

namespace PropertyGpsApi.Features.Properties;

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
/// The officer's own ward and zone are passed in, never read from claims here —
/// only the controller can see a token, and a service that reached for
/// HttpContext would be untestable.
/// </summary>
internal static class JurisdictionRules
{
    /// <summary>Ward-level officer. Scoped to a single ward.</summary>
    internal const int WardOfficer = 116;

    /// <summary>Zone-level officer. Legitimately has no ward claim at all.</summary>
    internal const int ZoneOfficer = 125;

    /// <summary>
    /// A ward officer may only act on their own ward. Without this the token
    /// authenticates but authorises nothing, and any officer could page through
    /// every ward in the city by changing three numbers in the request body.
    ///
    /// An officer with no ward claim is not blocked: role 116 should always
    /// carry one, and refusing on its absence would lock out a correctly
    /// configured officer over a data problem in the master tables.
    /// </summary>
    public static void RequireOwnWard(
        int roleId, long? officerWardId, int targetWardId, string message)
    {
        if (roleId != WardOfficer) return;
        if (officerWardId is null || officerWardId == targetWardId) return;

        throw ApiException.Forbidden(message, ApiErrorCodes.OutsideJurisdiction);
    }

    /// <summary>
    /// Whether this officer may read a document belonging to the given
    /// zone/ward. Any role not listed is refused outright rather than
    /// defaulting to allowed.
    /// </summary>
    public static bool MayViewMedia(
        int roleId, long? officerWardId, long? officerZoneId, MediaScope scope) => roleId switch
    {
        WardOfficer => officerWardId is not { } ward || ward == scope.WardId,
        ZoneOfficer => officerZoneId is not { } zone || zone == scope.ZoneId,
        _ => false
    };
}
