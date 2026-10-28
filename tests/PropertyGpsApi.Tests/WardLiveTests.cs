using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PropertyGpsApi.Common;
using PropertyGpsApi.Endpoints;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Infrastructure.Security;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;
using PropertyGpsApi.Services;

namespace PropertyGpsApi.Tests;

/// <summary>
/// Live allotments: an allot or unassign accepted in a ward is pushed, over a WebSocket, to
/// every phone that has that ward's list open. The hub is exercised with real sockets on a
/// loopback connection; the endpoint's refusals before the upgrade with a plain HttpContext.
/// </summary>
public class WardLiveTests
{
    private static readonly WardSyncHolderDto Taken = new() { AppId = 12, AssignedToUserId = 11333 };

    /// <summary>A connected server/client WebSocket pair over a loopback TCP connection.</summary>
    private sealed class SocketPair : IAsyncDisposable
    {
        public required WebSocket Server { get; init; }
        public required WebSocket Client { get; init; }
        private TcpClient? _a, _b;

        public static async Task<SocketPair> OpenAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            return new SocketPair
            {
                Server = WebSocket.CreateFromStream(server.GetStream(), new WebSocketCreationOptions { IsServer = true }),
                Client = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false }),
                _a = server, _b = client,
            };
        }

        public async ValueTask DisposeAsync()
        {
            Server.Dispose(); Client.Dispose(); _a?.Dispose(); _b?.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>The officer's wards; changed by a test to move the officer.</summary>
    private sealed class MappedOfficer(params Jurisdiction[] wards) : IOfficerService
    {
        public Jurisdiction[] Wards { get; set; } = wards;
        public bool Unreadable { get; set; }

        public Task<IReadOnlyList<Jurisdiction>> MappedWardsAsync(long officerId, CancellationToken ct) =>
            Unreadable
                ? throw new TimeoutException("database")
                : Task.FromResult<IReadOnlyList<Jurisdiction>>(Wards);
        public Task<bool> OfficerExistsAsync(string mobile, int roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> StoreOtpAsync(string mobile, string otp, CancellationToken ct) => throw new NotSupportedException();
        public Task<OtpValidationResult> ValidateOtpAsync(string mobile, string otp, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordLoginAsync(Officer officer, string? clientIp, CancellationToken ct) => throw new NotSupportedException();
        public Task<Officer?> LoadAsync(string mobile, CancellationToken ct) => throw new NotSupportedException();
        public Task<VerifyOtpResponse> ProfileAsync(string? mobile, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The host's stop signal, raised by a test as a recycle or redeploy would.</summary>
    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
    }

    private static readonly Jurisdiction Hoodi = new() { UlbCode = 1, WardNumber = "54", WardName = "Hoodi" };

    /// <summary>A request from officer 11333, with what the endpoint resolves.</summary>
    private static DefaultHttpContext Request(
        IHttpWebSocketFeature upgrade, string query, MappedOfficer officer, TimeProvider? clock = null,
        IHostApplicationLifetime? lifetime = null)
    {
        clock ??= TimeProvider.System;
        var services = new ServiceCollection()
            .AddSingleton<IOfficerService>(officer)
            .AddSingleton<IWardLiveHub>(new WardLiveHub(NullLogger<WardLiveHub>.Instance, clock))
            .AddSingleton(clock)
            .AddSingleton(lifetime ?? new Lifetime())
            .AddSingleton(new SessionValidator(null!, Options.Create(new AuthOptions { SingleDeviceSessions = false }),
                NullLogger<SessionValidator>.Instance))
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Features.Set(upgrade);
        http.Request.QueryString = new QueryString(query);
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(GpsClaims.UserId, "11333")], "test"));
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static async Task<(WebSocketMessageType Type, string Text, WebSocketCloseStatus? Close)?> ReceiveAsync(
        WebSocket socket, int timeoutMs = 2000)
    {
        var buffer = new byte[4096];
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            var r = await socket.ReceiveAsync(buffer, cts.Token);
            return (r.MessageType, Encoding.UTF8.GetString(buffer, 0, r.Count), r.CloseStatus);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public class TheHub
    {
        private static WardLiveHub Hub(TimeProvider? clock = null) =>
            new(NullLogger<WardLiveHub>.Instance, clock ?? TimeProvider.System);

        [Fact]
        public async Task A_push_reaches_every_phone_of_that_ward_and_no_other()
        {
            await using var a1 = await SocketPair.OpenAsync();
            await using var a2 = await SocketPair.OpenAsync();
            await using var b = await SocketPair.OpenAsync();
            var hub = Hub();
            hub.Add(1, "54", 11331, a1.Server);
            hub.Add(1, "54", 11333, a2.Server);
            hub.Add(1, "55", 11320, b.Server);

            var delivered = await hub.PublishAsync(1, "54", [Taken]);

            Assert.Equal(2, delivered);
            foreach (var phone in new[] { a1.Client, a2.Client })
            {
                var got = await ReceiveAsync(phone);
                Assert.NotNull(got);
                Assert.Equal(WebSocketMessageType.Text, got.Value.Type);
                using var doc = JsonDocument.Parse(got.Value.Text);
                Assert.Equal("holders", doc.RootElement.GetProperty("type").GetString());
                var holder = doc.RootElement.GetProperty("holders")[0];
                Assert.Equal(12, holder.GetProperty("appId").GetInt32());
                Assert.Equal(11333, holder.GetProperty("assignedToUserId").GetInt32());
            }
            Assert.Null(await ReceiveAsync(b.Client, 300));
        }

        [Fact]
        public async Task A_phone_that_has_gone_is_dropped_and_the_others_still_get_it()
        {
            await using var gone = await SocketPair.OpenAsync();
            await using var here = await SocketPair.OpenAsync();
            var hub = Hub();
            hub.Add(1, "54", 11331, gone.Server);
            hub.Add(1, "54", 11333, here.Server);
            gone.Server.Abort();

            var delivered = await hub.PublishAsync(1, "54", [Taken]);

            Assert.Equal(1, delivered);
            Assert.Equal(1, hub.Count(1, "54"));
            Assert.NotNull(await ReceiveAsync(here.Client));
        }

        [Fact]
        public async Task A_second_socket_of_the_same_officer_closes_the_first()
        {
            await using var first = await SocketPair.OpenAsync();
            await using var second = await SocketPair.OpenAsync();
            var hub = Hub();
            hub.Add(1, "54", 11333, first.Server);
            hub.Add(1, "54", 11333, second.Server);

            var closed = await ReceiveAsync(first.Client);
            Assert.NotNull(closed);
            Assert.Equal(WebSocketMessageType.Close, closed.Value.Type);
            Assert.Equal(WardLiveConnection.ReplacedByNewer, closed.Value.Close);
            Assert.Equal(1, hub.Count(1, "54"));
        }

        [Fact]
        public async Task A_replaced_socket_that_never_answers_is_dropped_after_the_grace()
        {
            await using var first = await SocketPair.OpenAsync();
            await using var second = await SocketPair.OpenAsync();
            var clock = new FakeTimeProvider();
            var hub = Hub(clock);
            hub.Add(1, "54", 11333, first.Server);
            hub.Add(1, "54", 11333, second.Server);

            // The first phone never reads, so never answers the close.
            for (var i = 0; i < 100 && first.Server.State != WebSocketState.Aborted; i++)
            {
                clock.Advance(WardLiveConnection.CloseGrace);
                await Task.Delay(20);
            }
            Assert.Equal(WebSocketState.Aborted, first.Server.State);
            Assert.Equal(WebSocketState.Open, second.Server.State);
        }

        [Fact]
        public async Task Removing_a_socket_stops_its_pushes()
        {
            await using var pair = await SocketPair.OpenAsync();
            var hub = Hub();
            var connection = hub.Add(1, "54", 11333, pair.Server);
            hub.Remove(connection);

            Assert.Equal(0, await hub.PublishAsync(1, "54", [Taken]));
            Assert.Null(await ReceiveAsync(pair.Client, 300));
        }

        [Fact]
        public async Task A_ward_with_nobody_listening_is_not_an_error() =>
            Assert.Equal(0, await Hub().PublishAsync(1, "54", [Taken]));

        /// <summary>
        /// A ward is its ULB and its ward number as text: "12A", " 12a " and "12a" are one
        /// ward, as they are to the database; the same number in another ULB is another ward.
        /// </summary>
        [Fact]
        public async Task The_hub_is_keyed_by_ulb_and_ward_text_ignoring_case_and_spaces()
        {
            await using var here = await SocketPair.OpenAsync();
            await using var otherUlb = await SocketPair.OpenAsync();
            var hub = Hub();
            var connection = hub.Add(1, " 12A ", 11333, here.Server);
            hub.Add(2, "12A", 11320, otherUlb.Server);

            Assert.Equal((1, "12A"), (connection.UlbCode, connection.WardNumber));
            Assert.Equal(1, hub.Count(1, "12a"));
            Assert.Equal(1, await hub.PublishAsync(1, "12a ", [Taken]));
            Assert.NotNull(await ReceiveAsync(here.Client));
            Assert.Null(await ReceiveAsync(otherUlb.Client, 300));
            Assert.Equal(0, await hub.PublishAsync(1, "12", [Taken]));
        }

        [Fact]
        public async Task The_same_officer_in_one_ward_spelt_two_ways_is_one_socket()
        {
            await using var first = await SocketPair.OpenAsync();
            await using var second = await SocketPair.OpenAsync();
            var hub = Hub();
            hub.Add(1, "12a", 11333, first.Server);
            hub.Add(1, "12A", 11333, second.Server);

            var closed = await ReceiveAsync(first.Client);
            Assert.Equal(WardLiveConnection.ReplacedByNewer, closed?.Close);
            Assert.Equal(1, hub.Count(1, "12A"));
        }

        [Fact]
        public void The_key_comparer_ignores_case_and_the_hash_agrees()
        {
            var comparer = WardLiveHub.WardKeyComparer.Instance;
            var a = WardLiveHub.Key(1, " 12a ");
            var b = WardLiveHub.Key(1, "12A");

            Assert.True(comparer.Equals(a, b));
            Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
            Assert.False(comparer.Equals(a, WardLiveHub.Key(2, "12A")));
            Assert.False(comparer.Equals(a, WardLiveHub.Key(1, "12")));
        }

        [Fact]
        public void The_message_is_the_pull_downs_shape_under_a_type() =>
            Assert.Equal("""{"type":"holders","holders":[{"appId":12,"assignedToUserId":11333}]}""",
                Encoding.UTF8.GetString(WardLiveHub.Serialize([Taken])));
    }

    public class WhoMayConnect
    {
        /// <summary>Says it is a WebSocket request; accepting it means the checks let it through.</summary>
        private sealed class UpgradeRequest(bool isWebSocket) : IHttpWebSocketFeature
        {
            public bool IsWebSocketRequest => isWebSocket;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
                throw new InvalidOperationException("accepted");
        }

        private static DefaultHttpContext Request(bool isWebSocket, string query, params Jurisdiction[] mapped) =>
            WardLiveTests.Request(new UpgradeRequest(isWebSocket), query, new MappedOfficer(mapped));

        private static string Code(DefaultHttpContext http)
        {
            http.Response.Body.Position = 0;
            using var doc = JsonDocument.Parse(http.Response.Body);
            return doc.RootElement.GetProperty("code").GetString()!;
        }

        [Fact]
        public async Task A_plain_request_is_told_this_address_is_a_WebSocket()
        {
            var http = Request(isWebSocket: false, "?ulbCode=1&wardNumber=54", Hoodi);
            await WardLiveEndpoint.HandleAsync(http);
            Assert.Equal(400, http.Response.StatusCode);
            Assert.Equal(ApiErrorCodes.BadRequest, Code(http));
        }

        [Theory]
        [InlineData("")]
        [InlineData("?ulbCode=1")]
        [InlineData("?wardNumber=54")]
        [InlineData("?ulbCode=0&wardNumber=54")]
        [InlineData("?ulbCode=abc&wardNumber=54")]
        [InlineData("?ulbCode=1&wardNumber=%20%20")]
        [InlineData("?ulbCode=1&wardNumber=123456789012345678901")]   // 21 characters
        public async Task A_missing_ward_is_refused_before_the_upgrade(string query)
        {
            var http = Request(isWebSocket: true, query, Hoodi);
            await WardLiveEndpoint.HandleAsync(http);
            Assert.Equal(400, http.Response.StatusCode);
        }

        [Fact]
        public async Task A_ward_the_officer_is_not_mapped_to_is_refused_before_the_upgrade()
        {
            var http = Request(isWebSocket: true, "?ulbCode=1&wardNumber=112", Hoodi);
            await WardLiveEndpoint.HandleAsync(http);
            Assert.Equal(403, http.Response.StatusCode);
            Assert.Equal(ApiErrorCodes.OutsideJurisdiction, Code(http));
        }

        [Fact]
        public async Task The_officers_own_ward_is_let_through_to_the_upgrade()
        {
            var http = Request(isWebSocket: true, "?ulbCode=1&wardNumber=54", Hoodi);
            var accepted = await Assert.ThrowsAsync<InvalidOperationException>(() => WardLiveEndpoint.HandleAsync(http));
            Assert.Equal("accepted", accepted.Message);
        }

        /// <summary>A two-ward officer's second ward, URL-encoded text in another case.</summary>
        [Fact]
        public async Task Any_mapped_ward_is_let_through_matched_as_text()
        {
            var second = new Jurisdiction { UlbCode = 7, WardNumber = "12A/1" };
            var http = Request(isWebSocket: true, "?ulbCode=7&wardNumber=%2012a%2F1%20", Hoodi, second);
            var accepted = await Assert.ThrowsAsync<InvalidOperationException>(() => WardLiveEndpoint.HandleAsync(http));
            Assert.Equal("accepted", accepted.Message);
        }

        [Fact]
        public async Task The_same_ward_number_in_another_ulb_is_refused()
        {
            var http = Request(isWebSocket: true, "?ulbCode=2&wardNumber=54", Hoodi);
            await WardLiveEndpoint.HandleAsync(http);
            Assert.Equal(403, http.Response.StatusCode);
        }

        [Fact]
        public void The_ward_is_read_from_the_query_trimmed()
        {
            var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["ulbCode"] = "7", ["wardNumber"] = " 12A "
            });

            Assert.True(WardLiveEndpoint.TryReadWard(query, out var ulb, out var ward));
            Assert.Equal((7, "12A"), (ulb, ward));
        }
    }

    public class WhenTheSocketIsClosed
    {
        private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

        [Theory]
        [InlineData(SessionCheck.Replaced, "SESSION_REPLACED")]
        [InlineData(SessionCheck.NotTracked, "Session expired")]
        public void A_session_that_has_ended_closes_the_socket_with_4001_and_says_which(SessionCheck check, string reason)
        {
            var end = WardLiveEndpoint.EndOf(check, Now.AddDays(1), Now, stillMapped: true);
            Assert.Equal((WardLiveConnection.SessionEnded, reason), end);
        }

        [Theory]
        [InlineData(SessionCheck.Current)]
        [InlineData(SessionCheck.Unavailable)]   // a database hiccup signs nobody out
        public void A_current_session_keeps_the_socket(SessionCheck check) =>
            Assert.Null(WardLiveEndpoint.EndOf(check, Now.AddDays(1), Now, stillMapped: true));

        [Fact]
        public void An_expired_token_closes_the_socket_even_if_the_session_is_current() =>
            Assert.Equal((WardLiveConnection.SessionEnded, WardLiveEndpoint.SessionExpiredReason),
                WardLiveEndpoint.EndOf(SessionCheck.Current, Now.AddSeconds(-1), Now, stillMapped: true));

        [Fact]
        public void An_officer_no_longer_mapped_to_the_ward_is_closed_with_4003() =>
            Assert.Equal(WardLiveConnection.NotMapped,
                WardLiveEndpoint.EndOf(SessionCheck.Current, Now.AddDays(1), Now, stillMapped: false)?.Status);

        [Fact]
        public void An_ended_session_wins_over_the_ward_so_the_phone_signs_out() =>
            Assert.Equal(WardLiveConnection.SessionEnded,
                WardLiveEndpoint.EndOf(SessionCheck.Replaced, Now.AddDays(1), Now, stillMapped: false)?.Status);

        [Fact]
        public void The_tokens_expiry_is_read_from_its_exp_claim()
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(JwtRegisteredClaimNames.Exp, Now.ToUnixTimeSeconds().ToString())], "test"));
            Assert.Equal(Now, WardLiveEndpoint.ExpiresAt(user));
        }

        [Fact]
        public void The_close_codes_are_the_applications_own() =>
            Assert.Equal((4001, 4002, 4003),
                ((int)WardLiveConnection.SessionEnded, (int)WardLiveConnection.ReplacedByNewer, (int)WardLiveConnection.NotMapped));
    }

    /// <summary>The endpoint with a real socket: what it does after the upgrade.</summary>
    public class WhileConnected : IAsyncLifetime
    {
        /// <summary>Hands the endpoint the server end of a real socket pair.</summary>
        private sealed class Upgraded(WebSocket socket) : IHttpWebSocketFeature
        {
            public bool IsWebSocketRequest => true;
            public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
        }

        private SocketPair _pair = null!;
        private readonly FakeTimeProvider _clock = new();
        private readonly Lifetime _lifetime = new();
        private readonly MappedOfficer _officer = new(Hoodi);
        private Task _handler = Task.CompletedTask;

        public async Task InitializeAsync()
        {
            _pair = await SocketPair.OpenAsync();
            // Runs up to the first wait on the phone; the session watch is already waiting.
            _handler = WardLiveEndpoint.HandleAsync(
                Request(new Upgraded(_pair.Server), "?ulbCode=1&wardNumber=54", _officer, _clock, _lifetime));
        }

        public async Task DisposeAsync() => await _pair.DisposeAsync();

        private async Task<bool> EndsWithinAsync(int ms) =>
            await Task.WhenAny(_handler, Task.Delay(ms)) == _handler;

        [Fact]
        public async Task The_server_stopping_tells_the_phone_to_reconnect_and_the_request_ends()
        {
            _lifetime.StopApplication();

            var closed = await ReceiveAsync(_pair.Client);
            Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, closed?.Close);
            await _pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
            Assert.True(await EndsWithinAsync(2000));
        }

        [Fact]
        public async Task A_phone_that_does_not_answer_the_shutdown_is_dropped_after_the_grace()
        {
            _lifetime.StopApplication();

            for (var i = 0; i < 50 && !_handler.IsCompleted; i++)
            {
                _clock.Advance(WardLiveEndpoint.ShutdownGrace);
                await Task.Delay(20);
            }
            Assert.True(_handler.IsCompleted);
            Assert.Equal(WebSocketState.Aborted, _pair.Server.State);
        }

        [Fact]
        public async Task An_officer_moved_off_the_ward_is_closed_with_4003_at_the_next_check()
        {
            _officer.Wards = [];
            _clock.Advance(WardLiveEndpoint.RecheckEvery);

            var closed = await ReceiveAsync(_pair.Client);
            Assert.Equal(WardLiveConnection.NotMapped, closed?.Close);
            await _pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", CancellationToken.None);
            Assert.True(await EndsWithinAsync(2000));
        }

        [Fact]
        public async Task Wards_that_cannot_be_read_keep_the_socket()
        {
            _officer.Unreadable = true;
            _clock.Advance(WardLiveEndpoint.RecheckEvery);

            // No close sent: the server end is still Open, not CloseSent.
            await Task.Delay(300);
            Assert.Equal(WebSocketState.Open, _pair.Server.State);
            Assert.False(_handler.IsCompleted);
        }

        [Fact]
        public async Task A_mapped_officer_with_a_current_session_keeps_the_socket()
        {
            _clock.Advance(WardLiveEndpoint.RecheckEvery);

            await Task.Delay(300);
            Assert.Equal(WebSocketState.Open, _pair.Server.State);
            Assert.False(_handler.IsCompleted);
        }
    }

    public class ThePublishAfterAnAllot
    {
        [Fact]
        public void Reads_the_ward_and_the_holder_by_the_pull_downs_rule()
        {
            var sql = ApplicationService.AppHolderSql;
            Assert.Contains("md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID", sql);
            Assert.Contains("a.IsActive = 1", sql);
            Assert.Contains("NOT (a.Status = 'Completed' AND ap.App_Status = 400)", sql);
            Assert.Contains("ISNULL(h.Arch_Id, 0)", sql);
        }

        /// <summary>The hub's key, read as the ward sync filters: MD_ULBCode and the trimmed MD_WardId text.</summary>
        [Fact]
        public void Reads_the_ulb_and_the_ward_number_as_text()
        {
            var sql = ApplicationService.AppHolderSql;
            Assert.Contains("UlbCode = TRY_CONVERT(int, md.MD_ULBCode)", sql);
            Assert.Contains("WardNumber = LTRIM(RTRIM(CONVERT(nvarchar(20), md.MD_WardId)))", sql);
            Assert.DoesNotContain("MD_ZoneId", sql);
        }

        [Fact]
        public void Writes_nothing()
        {
            foreach (var word in new[] { "INSERT", "UPDATE", "DELETE", "EXEC" })
                Assert.DoesNotContain(word, ApplicationService.AppHolderSql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
