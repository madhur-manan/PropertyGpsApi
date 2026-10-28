namespace PropertyGpsApi.Interfaces;

/// <summary>
/// One phone per officer, kept in dbo.GpsOfficerSession (master database): the phone each
/// officer is bound to, and the sign-in live on it. Separate from IOfficerService so its
/// test fakes are untouched.
/// </summary>
public interface IOfficerSessionStore
{
    /// <summary>
    /// Records a new sign-in. An officer with no phone yet is bound to this one; any other
    /// phone is refused (false) until an administrator clears the binding. With
    /// <paramref name="enforce"/> false the sign-in is always recorded, and the binding is
    /// left as it is.
    /// </summary>
    Task<bool> TryStartAsync(
        long officerId, Guid sessionId, string? deviceId, string? clientIp, bool enforce, CancellationToken ct);

    /// <summary>Whether this is still the officer's current session.</summary>
    Task<bool> IsCurrentAsync(long officerId, Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Ends the session, only if it is still the current one. The phone binding stays.
    /// </summary>
    Task EndAsync(long officerId, Guid sessionId, CancellationToken ct);
}
