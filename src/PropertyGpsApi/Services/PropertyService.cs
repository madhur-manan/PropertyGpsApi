using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// The mobile fetch, over USP_S_GetAppDetails: applications at App_Status 10 with the
/// processing fee paid that no device has taken delivery of yet, scoped to one ward.
/// </summary>
internal sealed class PropertyService(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    IAssignmentReader assignments,
    IOwnerReader owners,
    ILogger<PropertyService> logger) : IPropertyService
{
    /// <summary>
    /// Passed as @AppID, which the procedure uses as TOP (n) rather than as an identifier.
    /// A ward's undelivered backlog is small, but the cap stops a first sync from becoming
    /// unbounded if that ever stops being true.
    /// </summary>
    private const int RowLimit = 500;

    private const string RoadStateSql = """
        SELECT RowId = Rd_RoadRow_ID,
               Active = CAST(CASE WHEN ISNULL(Rd_RoadActive, 1) = 1 THEN 1 ELSE 0 END AS bit),
               EnteredRoadName = Rd_EnteredRoadName
        FROM dbo.BtoA_SiteRoadDetails WITH (NOLOCK)
        WHERE Rd_App_Id IN @appIds;
        """;

    public async Task<IReadOnlyList<PropertyDto>> FetchAsync(
        FetchApplicationsRequest request,
        long officerId,
        int roleId,
        long? officerWardId,
        CancellationToken ct)
    {
        JurisdictionRules.RequireOwnWard(
            roleId, officerWardId, request.WardId,
            "You can only view applications for your own ward.");

        var p = new DynamicParameters();
        p.Add("@AppID", RowLimit, DbType.Int32);
        p.Add("@Epid",
            string.IsNullOrWhiteSpace(request.Epid) ? null : request.Epid.Trim(),
            DbType.String, size: 20);
        p.Add("@zoneId", request.ZoneId, DbType.Int32);
        p.Add("@wardId", request.WardId, DbType.Int32);
        p.Add("@Level", 1, DbType.Int32);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var rows = PropertyMapper.DropPossiblyTruncatedLast((await connection.QueryAsync<PropertyRow>(
            Sp.Call(procedures.Value.FetchApplications, p, ct, timeoutSeconds: 120))).AsList(), RowLimit);

        // A corner plot joins to one row per declared road, so the procedure returns several
        // rows for the same application. Group rather than letting the application duplicate.
        var grouped = rows.GroupBy(r => r.AppID).ToList();

        var appIds = grouped.Select(g => g.Key).ToList();

        // The procedure keeps inactive road rows and leaves out the typed road name; the ward
        // sync does neither. Read both straight from the road table so the two paths agree.
        if (appIds.Count > 0)
        {
            var state = (await connection.QueryAsync<RoadRowState>(new CommandDefinition(
                    RoadStateSql, new { appIds }, cancellationToken: ct, commandTimeout: 60)))
                .GroupBy(s => s.RowId)
                .ToDictionary(g => g.Key, g => g.First());
            PropertyMapper.ApplyRoadState(rows, state);
        }
        var assignmentsByApp = await assignments.ActiveForAsync(connection, appIds, ct);
        var ownersByApp = await owners.ForAsync(connection, appIds, ct);

        logger.LogInformation(
            "Fetched {Apps} applications ({Rows} rows) for officer {OfficerId} zone {Zone} ward {Ward}",
            grouped.Count, rows.Count, officerId, request.ZoneId, request.WardId);

        return grouped.Select(g => PropertyMapper.Map(g, assignmentsByApp, ownersByApp, officerId)).ToList();
    }
}
