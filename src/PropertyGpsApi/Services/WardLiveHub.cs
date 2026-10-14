using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Services;

/// <summary>
/// In memory: the API runs as one IIS worker process. A recycle or a redeploy closes every
/// socket (WardLiveEndpoint); the phones reconnect and catch up with the pull-down call.
/// </summary>
internal sealed class WardLiveHub(ILogger<WardLiveHub> logger, TimeProvider clock) : IWardLiveHub
{
    /// <summary>A phone that does not take a push within this long is dropped.</summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<(int Zone, int Ward), ConcurrentDictionary<Guid, WardLiveConnection>> _wards = new();

    public WardLiveConnection Add(int zoneId, int wardId, long officerId, WebSocket socket)
    {
        var connection = new WardLiveConnection(zoneId, wardId, officerId, socket, clock);
        var ward = _wards.GetOrAdd((zoneId, wardId), _ => new ConcurrentDictionary<Guid, WardLiveConnection>());

        // One per officer per ward: an app reopened without the old socket closing (killed,
        // network switched) must not leave a stale one that receives everything twice. Ended,
        // not only closed: one that never answers the close must not stay open on the server.
        foreach (var older in ward.Values.Where(c => c.OfficerId == officerId).ToList())
        {
            if (ward.TryRemove(older.Id, out _))
                _ = older.EndAsync(WardLiveConnection.ReplacedByNewer, "Replaced by a newer connection",
                    WardLiveConnection.CloseGrace);
        }

        ward[connection.Id] = connection;
        logger.LogInformation(
            "Ward live: officer {OfficerId} connected to {Zone}/{Ward} ({Count} open)",
            officerId, zoneId, wardId, ward.Count);
        return connection;
    }

    public void Remove(WardLiveConnection connection)
    {
        if (_wards.TryGetValue((connection.ZoneId, connection.WardId), out var ward)
            && ward.TryRemove(connection.Id, out _))
            logger.LogInformation(
                "Ward live: officer {OfficerId} disconnected from {Zone}/{Ward} ({Count} open)",
                connection.OfficerId, connection.ZoneId, connection.WardId, ward.Count);
    }

    public int Count(int zoneId, int wardId) =>
        _wards.TryGetValue((zoneId, wardId), out var ward) ? ward.Count : 0;

    public async Task<int> PublishAsync(int zoneId, int wardId, IReadOnlyList<WardSyncHolderDto> holders)
    {
        try
        {
            if (holders.Count == 0 || !_wards.TryGetValue((zoneId, wardId), out var ward) || ward.IsEmpty)
                return 0;

            var message = Serialize(holders);
            var targets = ward.Values.ToList();
            var sent = await Task.WhenAll(targets.Select(async c =>
            {
                if (await c.TrySendAsync(message, SendTimeout)) return true;
                ward.TryRemove(c.Id, out _);
                return false;
            }));

            var delivered = sent.Count(ok => ok);
            logger.LogInformation(
                "Ward live: pushed {Holders} holder(s) to {Delivered} of {Sockets} socket(s) in {Zone}/{Ward}",
                holders.Count, delivered, targets.Count, zoneId, wardId);
            return delivered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ward live: push to {Zone}/{Ward} failed", zoneId, wardId);
            return 0;
        }
    }

    /// <summary>The same shape as POST ward-sync/holders answers, under a type.</summary>
    internal static byte[] Serialize(IReadOnlyList<WardSyncHolderDto> holders) =>
        JsonSerializer.SerializeToUtf8Bytes(new WardLiveMessage { Holders = holders });

    internal sealed class WardLiveMessage
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "holders";
        [JsonPropertyName("holders")] public IReadOnlyList<WardSyncHolderDto> Holders { get; init; } = [];
    }
}

/// <summary>One phone's socket for one ward.</summary>
internal sealed class WardLiveConnection(int zoneId, int wardId, long officerId, WebSocket socket, TimeProvider clock)
{
    /// <summary>
    /// Close codes the app acts on (4000-4999 are the application's own). SessionEnded signs
    /// the phone out; the other two only stop the list listening.
    /// </summary>
    internal const WebSocketCloseStatus SessionEnded = (WebSocketCloseStatus)4001;
    internal const WebSocketCloseStatus ReplacedByNewer = (WebSocketCloseStatus)4002;
    internal const WebSocketCloseStatus NotMapped = (WebSocketCloseStatus)4003;

    /// <summary>After a server close, how long the phone gets to answer before the socket is dropped.</summary>
    internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();
    public int ZoneId { get; } = zoneId;
    public int WardId { get; } = wardId;
    public long OfficerId { get; } = officerId;
    public WebSocket Socket { get; } = socket;

    /// <summary>One frame, never two at once on a socket (WebSocket allows one send at a time).</summary>
    public async Task<bool> TrySendAsync(byte[] message, TimeSpan timeout)
    {
        if (Socket.State != WebSocketState.Open) return false;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _sendLock.WaitAsync(cts.Token);
            try
            {
                await Socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
                return true;
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            return false;
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        if (Socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _sendLock.WaitAsync(cts.Token);
            try
            {
                await Socket.CloseOutputAsync(status, reason, cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Closes with this code, and drops the socket if the phone has not answered within
    /// <paramref name="grace"/>: a phone that never answers must not keep its request open on
    /// the server. The phone's answer ends the handler, which cancels <paramref name="ct"/>.
    /// </summary>
    public async Task EndAsync(WebSocketCloseStatus status, string reason, TimeSpan grace, CancellationToken ct = default)
    {
        await CloseAsync(status, reason);
        try
        {
            await Task.Delay(grace, clock, ct);
        }
        catch (OperationCanceledException)
        {
            // The handler has finished.
        }
        if (Socket.State is not WebSocketState.Closed) Socket.Abort();
    }
}
