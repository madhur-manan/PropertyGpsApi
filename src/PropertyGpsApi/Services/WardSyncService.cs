using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;
using PropertyGpsApi.Interfaces;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Services;

/// <summary>
/// The new Fetch / Update, over USP_S_GpsWardSync and USP_U_GpsWardSyncAck.
///
/// The phone says which App_Ids it holds; the server answers with the next batch of
/// workable applications in the ward that it lacks. IsPushedToGps plays no part in what
/// is sent - it is only written, by the acknowledgement, once the phone has saved a batch.
///
/// The old fetch (PropertyService over USP_S_GetAppDetails) is untouched and still serves
/// builds without WARD_SYNC.
/// </summary>
internal sealed class WardSyncService(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    IOfficerService officers,
    IAssignmentReader assignments,
    IOwnerReader owners,
    ILogger<WardSyncService> logger) : IWardSyncService
{
    public async Task<WardSyncResponse> PageAsync(
        WardSyncRequest request, long officerId, CancellationToken ct)
    {
        RequirePositive(request.KnownAppIds, "knownAppIds");
        RequirePositive(request.ReturnedAppIds, "returnedAppIds");
        RequirePositive(request.RefreshAppIds, "refreshAppIds");
        await RequireMappedWardAsync(officerId, request.ZoneId, request.WardId, ct);

        var p = new DynamicParameters();
        p.Add("@ZoneId", request.ZoneId, DbType.Int32);
        p.Add("@WardId", request.WardId, DbType.Int32);
        p.Add("@LocalCount", request.LocalCount, DbType.Int32);
        p.Add("@KnownAppIds", ToCsv(request.KnownAppIds), DbType.String, size: -1);
        p.Add("@ReturnedAppIds", ToCsv(request.ReturnedAppIds), DbType.String, size: -1);
        p.Add("@BatchSize", ClampBatch(request.BatchSize), DbType.Int32);
        p.Add("@IncludeClosed", request.IncludeClosed, DbType.Boolean);
        p.Add("@RefreshAppIds", ToCsv(request.RefreshAppIds), DbType.String, size: -1);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        WardSyncSummaryRow summary;
        List<PropertyRow> rows;
        List<WardSyncClosedRow> closed;

        // Read all three result sets and release the reader before the follow-up queries:
        // the connection cannot run another command while the grid is still open.
        await using (var grid = await connection.QueryMultipleAsync(
            Sp.Call(procedures.Value.WardSync, p, ct, timeoutSeconds: 120)))
        {
            summary = await grid.ReadSingleAsync<WardSyncSummaryRow>();
            rows = (await grid.ReadAsync<PropertyRow>()).AsList();
            closed = (await grid.ReadAsync<WardSyncClosedRow>()).AsList();
        }

        // A corner plot comes back as one row per road. The procedure batches by
        // application, so every road of an application is in this batch.
        var grouped = rows.GroupBy(r => r.AppID).ToList();
        var appIds = grouped.Select(g => g.Key).ToList();
        var assignmentsByApp = await assignments.ActiveForAsync(connection, appIds, ct);
        var ownersByApp = await owners.ForAsync(connection, appIds, ct);

        // Once per sync, with the closed list: who holds what the phone already has.
        List<WardSyncHolderDto> holders = [];
        if (request.IncludeClosed)
        {
            var open = StillOpen(request.KnownAppIds, closed.Select(c => c.AppId));
            holders = Holders(open, await assignments.ActiveForAsync(connection, open, ct));
        }

        logger.LogInformation(
            "Ward sync for officer {OfficerId} zone {Zone} ward {Ward}: server {ServerCount}, phone {LocalCount} " +
            "(sent {Known} ids), missing {MissingCount}, sending {Apps} applications ({Rows} rows), {Closed} closed, " +
            "{Held} of {Holders} held",
            officerId, request.ZoneId, request.WardId, summary.ServerCount, summary.LocalCount,
            request.KnownAppIds.Count, summary.MissingCount, grouped.Count, rows.Count, closed.Count,
            holders.Count(h => h.AssignedToUserId != 0), holders.Count);

        return new WardSyncResponse
        {
            ServerCount = summary.ServerCount,
            LocalCount = summary.LocalCount,
            MissingCount = summary.MissingCount,
            Items = grouped.Select(g => PropertyMapper.Map(g, assignmentsByApp, ownersByApp, officerId)).ToList(),
            Closed = closed.Select(c => new WardSyncClosedDto { AppId = c.AppId, AppStatus = c.AppStatus }).ToList(),
            Holders = holders
        };
    }

    /// <summary>The applications the phone holds that are still workable here: not reported closed.</summary>
    internal static List<int> StillOpen(IEnumerable<int> known, IEnumerable<int> closed)
    {
        var gone = closed.ToHashSet();
        return known.Distinct().Where(id => !gone.Contains(id)).ToList();
    }

    /// <summary>
    /// One entry per application, 0 when nobody holds it - so a phone clears a holder that
    /// has released it, rather than only ever learning of new ones.
    /// </summary>
    internal static List<WardSyncHolderDto> Holders(
        IEnumerable<int> appIds, IReadOnlyDictionary<int, AssignmentRow> held) =>
        appIds.Select(id => new WardSyncHolderDto
        {
            AppId = id,
            AssignedToUserId = held.TryGetValue(id, out var row) ? row.Arch_Id : 0
        }).ToList();

    public async Task<WardSyncAckResponse> AckAsync(
        WardSyncAckRequest request, long officerId, CancellationToken ct)
    {
        RequirePositive(request.AppIds, "appIds");
        await RequireMappedWardAsync(officerId, request.ZoneId, request.WardId, ct);

        var p = new DynamicParameters();
        p.Add("@ZoneId", request.ZoneId, DbType.Int32);
        p.Add("@WardId", request.WardId, DbType.Int32);
        p.Add("@AppIds", ToCsv(request.AppIds), DbType.String, size: -1);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        var row = await connection.QuerySingleAsync<WardSyncAckRow>(
            Sp.Call(procedures.Value.WardSyncAck, p, ct));

        logger.LogInformation(
            "Ward sync ack from officer {OfficerId} zone {Zone} ward {Ward}: {Requested} saved on the phone, {Updated} newly recorded",
            officerId, request.ZoneId, request.WardId, row.Requested, row.Updated);

        return new WardSyncAckResponse { Requested = row.Requested, Updated = row.Updated };
    }

    /// <summary>
    /// Who holds each asked-for application now: its newest active row in
    /// BtoA_Architect_AssignedApp, or 0. Limited to applications in the requested ward, so the
    /// answer never names the holder of anything outside the officer's jurisdiction. The ids
    /// arrive as one string, so a large ward cannot pass SQL Server's parameter limit.
    /// </summary>
    internal const string HoldersSql = """
        SELECT AppId = ap.App_Id, Arch_Id = ISNULL(h.Arch_Id, 0)
        FROM (SELECT DISTINCT AppId = TRY_CAST(value AS int) FROM STRING_SPLIT(@appIds, ',')) ids
        JOIN dbo.BtoAMainApp ap WITH (NOLOCK) ON ap.App_Id = ids.AppId
        OUTER APPLY (SELECT TOP (1) a.Arch_Id
                     FROM dbo.BtoA_Architect_AssignedApp a WITH (NOLOCK)
                     WHERE a.AppId = ap.App_Id AND a.IsActive = 1
                     ORDER BY a.AssignmentDate DESC) h
        WHERE EXISTS (SELECT 1 FROM dbo.BtoA_EPIDMetaData md WITH (NOLOCK)
                      WHERE md.MD_APP_ID = ap.App_Id AND md.MD_MotherEPID = ap.App_MotherEPID
                        AND md.MD_ZoneId = @zoneId AND md.MD_WardId = @wardId);
        """;

    public async Task<WardHoldersResponse> HoldersAsync(
        WardHoldersRequest request, long officerId, CancellationToken ct)
    {
        RequirePositive(request.AppIds, "appIds");
        await RequireMappedWardAsync(officerId, request.ZoneId, request.WardId, ct);
        if (request.AppIds.Count == 0) return new WardHoldersResponse();

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);
        var rows = (await connection.QueryAsync<HolderRow>(new CommandDefinition(
            HoldersSql,
            new { appIds = ToCsv(request.AppIds), zoneId = request.ZoneId, wardId = request.WardId },
            commandTimeout: 60, cancellationToken: ct))).AsList();

        logger.LogInformation(
            "Holders for officer {OfficerId} zone {Zone} ward {Ward}: asked {Asked}, in the ward {Found}, {Held} held",
            officerId, request.ZoneId, request.WardId, request.AppIds.Count, rows.Count,
            rows.Count(r => r.Arch_Id != 0));

        return new WardHoldersResponse
        {
            Holders = rows.Select(r => new WardSyncHolderDto { AppId = r.AppId, AssignedToUserId = r.Arch_Id }).ToList()
        };
    }

    private sealed class HolderRow
    {
        public int AppId { get; init; }
        public int Arch_Id { get; init; }
    }

    /// <summary>
    /// The ward must be one the officer is mapped to (mst_Ofcr_WardMapping, else their own
    /// ward) - the same list their dropdown shows. Checked against the master tables rather
    /// than the token, which carries only one ward and so refused a two-ward RI's second.
    /// </summary>
    private async Task RequireMappedWardAsync(long officerId, int zoneId, int wardId, CancellationToken ct)
    {
        var wards = await officers.MappedWardsAsync(officerId, ct);
        if (!IsMapped(wards, zoneId, wardId))
            throw ApiException.Forbidden(
                "This ward is not mapped to you.", ApiErrorCodes.OutsideJurisdiction);
    }

    internal static bool IsMapped(IEnumerable<Jurisdiction> wards, int zoneId, int wardId) =>
        wards.Any(w => w.ZoneId == zoneId && w.WardId == wardId);

    internal static int ClampBatch(int requested) =>
        Math.Clamp(requested, 1, WardSyncLimits.MaxBatch);

    /// <summary>
    /// The procedures take ids as one comma-separated string (the database has no table
    /// types). Built only from validated ints, so there is no text to inject.
    /// </summary>
    internal static string ToCsv(IEnumerable<int> ids) =>
        string.Join(',', ids.Distinct());

    internal static void RequirePositive(IReadOnlyList<int> ids, string field)
    {
        if (ids.Any(id => id <= 0))
            throw ApiException.BadRequest($"'{field}' may only contain application ids greater than zero.");
    }
}
