using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Infrastructure.Security;

/// <summary>What the session check decided for one request.</summary>
public enum SessionCheck
{
    /// <summary>This is the officer's current session (or the check is switched off).</summary>
    Current,

    /// <summary>
    /// No longer the officer's current session: ended by an administrator moving the
    /// officer to another phone, or replaced by a new sign-in on the same phone.
    /// </summary>
    Replaced,

    /// <summary>A token without a session id: issued before sessions existed.</summary>
    NotTracked,

    /// <summary>The session table could not be read. Not the officer's fault.</summary>
    Unavailable
}

/// <summary>
/// One phone per officer. Runs once per authenticated request, from the JWT bearer
/// OnTokenValidated event: the token's sid must still be the officer's row in
/// dbo.GpsOfficerSession. The outcome is left in HttpContext.Items for OnChallenge, which
/// turns it into the envelope (<see cref="WriteChallengeAsync"/>).
/// </summary>
public sealed class SessionValidator(
    IOfficerSessionStore sessions, IOptions<AuthOptions> options, ILogger<SessionValidator> logger)
{
    public const string OutcomeKey = "pgps.session-check";

    internal const string ReplacedMessage = "Your session was ended. Please sign in again.";

    internal const string ExpiredMessage = "Your session has expired. Please sign in again.";

    internal const string UnavailableMessage =
        "Sign-in could not be checked right now. Please try again in a moment.";

    public async Task<SessionCheck> CheckAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (!options.Value.SingleDeviceSessions) return SessionCheck.Current;

        if (!long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var officerId)
            || !Guid.TryParse(user.FindFirstValue(GpsClaims.SessionId), out var sessionId))
            return SessionCheck.NotTracked;

        try
        {
            return await sessions.IsCurrentAsync(officerId, sessionId, ct)
                ? SessionCheck.Current
                : SessionCheck.Replaced;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A database hiccup must not sign anyone out: their unsent surveys would sit
            // until they noticed. The request is refused as retryable instead.
            logger.LogWarning(ex, "Could not check the session of officer {OfficerId}", officerId);
            return SessionCheck.Unavailable;
        }
    }

    /// <summary>
    /// The JWT challenge envelope. The code stays UNAUTHENTICATED for a replaced session so
    /// every installed app signs out; errors[0] says why, for apps that can tell the officer.
    /// </summary>
    public static Task WriteChallengeAsync(HttpContext http)
    {
        var outcome = http.Items.TryGetValue(OutcomeKey, out var value) && value is SessionCheck check
            ? check
            : SessionCheck.NotTracked;

        return outcome switch
        {
            SessionCheck.Replaced => ErrorEnvelopeWriter.WriteAsync(
                http, StatusCodes.Status401Unauthorized, ApiErrorCodes.AuthRequired, ReplacedMessage,
                retryable: true,
                errors: [new ApiError { Code = ApiErrorCodes.SessionReplaced, Message = ReplacedMessage }]),

            SessionCheck.Unavailable => ErrorEnvelopeWriter.WriteAsync(
                http, StatusCodes.Status503ServiceUnavailable, ApiErrorCodes.Upstream, UnavailableMessage,
                retryable: true),

            _ => ErrorEnvelopeWriter.WriteAsync(
                http, StatusCodes.Status401Unauthorized, ApiErrorCodes.AuthRequired, ExpiredMessage,
                retryable: true)
        };
    }
}
