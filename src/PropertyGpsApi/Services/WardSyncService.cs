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

        logger.LogInformation(
            "Ward sync for officer {OfficerId} zone {Zone} ward {Ward}: server {ServerCount}, phone {LocalCount} " +
            "(sent {Known} ids), missing {MissingCount}, sending {Apps} applications ({Rows} rows), {Closed} closed",
            officerId, request.ZoneId, request.WardId, summary.ServerCount, summary.LocalCount,
            request.KnownAppIds.Count, summary.MissingCount, grouped.Count, rows.Count, closed.Count);

        return new WardSyncResponse
        {
            ServerCount = summary.ServerCount,
            LocalCount = summary.LocalCount,
            MissingCount = summary.MissingCount,
            Items = grouped.Select(g => PropertyMapper.Map(g, assignmentsByApp, ownersByApp, officerId)).ToList(),
            Closed = closed.Select(c => new WardSyncClosedDto { AppId = c.AppId, AppStatus = c.AppStatus }).ToList()
        };
    }

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
