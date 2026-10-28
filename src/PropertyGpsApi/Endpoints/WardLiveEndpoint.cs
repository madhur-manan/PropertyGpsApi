using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Endpoints;

/// <summary>
/// GET .../propertyinfo/ward-sync/live?ulbCode=&amp;wardNumber= - a WebSocket the phone keeps
/// open while it shows a ward's property list. The ward number is text (URL-encoded), matched
/// trimmed and ignoring case. The server only pushes:
/// <c>{"type":"holders","holders":[{"appId":12,"assignedToUserId":11333}]}</c>, the shape the
/// pull-down already reads, after every allot or unassign accepted in that ward
/// (ApplicationService). Anything the phone sends is ignored, apart from closing.
///
/// Who may connect: the token is the normal bearer header (never the URL, which IIS logs),
/// checked by JwtBearer and the one-phone session check like any request; the ward must be
/// one the officer is mapped to (403 before the upgrade). Both are checked again every few
/// minutes: the socket is closed with 4001 once the session has ended or the token has
/// expired, so a phone signed out elsewhere stops receiving, and with 4003 once the officer
/// is no longer mapped to the ward.
///
/// When the API stops (a recycle, a redeploy) every socket is closed with 1001, so the worker
/// is not held open by them; the phones reconnect to the new one and catch up.
/// </summary>
internal static class WardLiveEndpoint
{
    internal const string Route = ApiRoutes.Base + "/propertyinfo/ward-sync/live";

    internal static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(5);

    /// <summary>On shutdown, how long the phone gets to answer: well inside the host's 30 s.</summary>
    internal static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3);

    /// <summary>The close reasons of 4001; the app tells the officer which it was.</summary>
    internal const string SessionReplacedReason = ApiErrorCodes.SessionReplaced;
    internal const string SessionExpiredReason = "Session expired";

    public static IEndpointConventionBuilder MapWardLive(this IEndpointRouteBuilder app) =>
        app.MapGet(Route, HandleAsync).RequireAuthorization();

    internal static async Task HandleAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var ct = http.RequestAborted;

        if (!http.WebSockets.IsWebSocketRequest)
        {
            await ErrorEnvelopeWriter.WriteAsync(http, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                "This address takes a WebSocket connection.");
            return;
        }
        if (!TryReadWard(http.Request.Query, out var ulbCode, out var wardNumber))
        {
            await ErrorEnvelopeWriter.WriteAsync(http, StatusCodes.Status400BadRequest, ApiErrorCodes.BadRequest,
                "ulbCode and wardNumber are required.");
            return;
        }

        var officerId = http.User.RequireLong(GpsClaims.UserId);
        var wards = await services.GetRequiredService<IOfficerService>().MappedWardsAsync(officerId, ct);
        if (!WardSyncService.IsMapped(wards, ulbCode, wardNumber))
        {
            await ErrorEnvelopeWriter.WriteAsync(http, StatusCodes.Status403Forbidden, ApiErrorCodes.OutsideJurisdiction,
                "This ward is not mapped to you.");
            return;
        }

        var hub = services.GetRequiredService<IWardLiveHub>();
        var clock = services.GetRequiredService<TimeProvider>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PropertyGpsApi.WardLive");
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();

        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        var connection = hub.Add(ulbCode, wardNumber, officerId, socket);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = WatchSessionAsync(http.User, connection, scopes, clock, logger, stop.Token);
        // An open socket is a request in flight, and the server waits for those before it stops.
        using var stopping = lifetime.ApplicationStopping.Register(() =>
            _ = connection.EndAsync(WebSocketCloseStatus.EndpointUnavailable, "Server restarting", ShutdownGrace, stop.Token));
        try
        {
            await ReceiveUntilClosedAsync(connection, stop.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The phone went away without closing: network lost, app killed.
        }
        finally
        {
            hub.Remove(connection);
            await stop.CancelAsync();
            try { await watch; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// ulbCode a positive number, wardNumber 1 to 20 characters once trimmed (the query string
    /// arrives already URL-decoded). The ward comes back trimmed.
    /// </summary>
    internal static bool TryReadWard(IQueryCollection query, out int ulbCode, out string wardNumber)
    {
        var ward = query["wardNumber"].ToString();
        wardNumber = WardNumbers.Clean(ward) ?? "";
        return int.TryParse(query["ulbCode"], out ulbCode) && ulbCode > 0
            && WardNumbers.IsValid(ward);
    }

    /// <summary>
    /// Reads until the phone closes. Nothing the phone sends means anything; reading is what
    /// notices it has gone, and what answers its close.
    /// </summary>
    private static async Task ReceiveUntilClosedAsync(WardLiveConnection connection, CancellationToken ct)
    {
        var socket = connection.Socket;
        var buffer = new byte[256];
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType != WebSocketMessageType.Close) continue;
            if (socket.State == WebSocketState.CloseReceived)
                await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bye");
            break;
        }
    }

    private static async Task WatchSessionAsync(
        ClaimsPrincipal user, WardLiveConnection connection, IServiceScopeFactory scopes, TimeProvider clock,
        ILogger logger, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(RecheckEvery, clock, ct);

            SessionCheck check;
            bool mapped;
            await using (var scope = scopes.CreateAsyncScope())
            {
                check = await scope.ServiceProvider.GetRequiredService<SessionValidator>().CheckAsync(user, ct);
                mapped = await StillMappedAsync(scope.ServiceProvider, connection, logger, ct);
            }

            if (EndOf(check, ExpiresAt(user), clock.GetUtcNow(), mapped) is not { } end) continue;

            logger.LogInformation(
                "Ward live: closing officer {OfficerId}'s socket in {Ulb}/{Ward}: {Reason} ({Check})",
                connection.OfficerId, connection.UlbCode, connection.WardNumber, end.Reason, check);
            await connection.EndAsync(end.Status, end.Reason, WardLiveConnection.CloseGrace, ct);
            return;
        }
    }

    /// <summary>The officer's wards read again; true when they cannot be read right now.</summary>
    private static async Task<bool> StillMappedAsync(
        IServiceProvider services, WardLiveConnection connection, ILogger logger, CancellationToken ct)
    {
        try
        {
            var wards = await services.GetRequiredService<IOfficerService>().MappedWardsAsync(connection.OfficerId, ct);
            return WardSyncService.IsMapped(wards, connection.UlbCode, connection.WardNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Ward live: could not re-read officer {OfficerId}'s wards", connection.OfficerId);
            return true;
        }
    }

    /// <summary>
    /// How the socket must close now, or null to keep it. The session first, since 4001 signs
    /// the phone out: the token's own expiry, then a session replaced or signed out. Then the
    /// ward (4003), which only stops the list listening. A database hiccup in either check
    /// (Unavailable, or wards that could not be read) keeps the socket: it must not sign
    /// anyone out.
    /// </summary>
    internal static (WebSocketCloseStatus Status, string Reason)? EndOf(
        SessionCheck check, DateTimeOffset? expiresAt, DateTimeOffset now, bool stillMapped)
    {
        if (expiresAt is { } exp && now >= exp) return (WardLiveConnection.SessionEnded, SessionExpiredReason);
        if (check is SessionCheck.Replaced) return (WardLiveConnection.SessionEnded, SessionReplacedReason);
        if (check is SessionCheck.NotTracked) return (WardLiveConnection.SessionEnded, SessionExpiredReason);
        if (!stillMapped) return (WardLiveConnection.NotMapped, "Ward no longer mapped");
        return null;
    }

    internal static DateTimeOffset? ExpiresAt(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Exp), out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
