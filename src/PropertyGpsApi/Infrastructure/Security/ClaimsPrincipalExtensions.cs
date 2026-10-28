using System.Security.Claims;
using PropertyGpsApi.Common;

namespace PropertyGpsApi.Infrastructure.Security;

/// <summary>
/// Reading the officer's identity off the token.
///
/// Lives here beside <see cref="GpsClaims"/> because the claim names and the
/// code that parses them belong together, and because two features now need it.
/// It previously sat at the bottom of PropertyInfoController, which meant
/// AppController had to take a dependency on the Properties feature to reach a
/// two-line helper.
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// A claim the request cannot proceed without. A token missing it is not a
    /// bad request - it is a token we should not have accepted, so the officer
    /// is asked to sign in again rather than shown a validation error.
    /// </summary>
    public static long RequireLong(this ClaimsPrincipal user, string claimType) =>
        long.TryParse(user.FindFirstValue(claimType), out var value)
            ? value
            : throw ApiException.Unauthorized("Your session is not valid. Please sign in again.");

    /// <summary>
    /// A claim that is legitimately absent for some roles - an officer above ward
    /// level has no ward, and JwtTokenService omits the claim rather than writing a zero.
    /// </summary>
    public static long? OptionalLong(this ClaimsPrincipal user, string claimType) =>
        long.TryParse(user.FindFirstValue(claimType), out var value) ? value : null;

    /// <summary>
    /// A text claim that may be absent - the ward number (GpsClaims.WardNumber), which is
    /// text, not a number. Trimmed; null when absent or blank.
    /// </summary>
    public static string? OptionalString(this ClaimsPrincipal user, string claimType) =>
        user.FindFirstValue(claimType) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
