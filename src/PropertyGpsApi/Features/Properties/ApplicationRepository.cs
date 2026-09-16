using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Properties;

public interface IApplicationRepository
{
    Task<IReadOnlyList<ApplicationDto>> FetchAsync(
        FetchApplicationsRequest request, long officerId, int roleId, CancellationToken ct);

    Task<AssignOutcome> AssignAsync(
        int appId, long officerId, int roleId, string? officerName, bool assign, CancellationToken ct);
}

/// <summary>
/// What USP_IU_Architect_AssignedApp reports back. StatusCode is 200 on success and 400 for
/// a business rejection - already assigned, or the officer queue is full.
/// </summary>
public sealed record AssignOutcome(int StatusCode, int AssignId, int AppId, string? Message)
{
    public bool Succeeded => StatusCode == 200;
}

internal sealed class ApplicationRepository(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<ApplicationRepository> logger) : IApplicationRepository
{
    /// <summary>
    /// USP_S_BtoA_GpsDashData_v1 multiplexes six different queries on @p_Level, and each
    /// branch is written for one officer role. Mapping role to level here keeps that BBMP
    /// convention out of the rest of the codebase.
    ///   130 -> 1, Commissioner -> 2, 116 (ward case worker) -> 5, 125 (Joint Comm) -> 6.
    /// Levels 3 and 4 are reporting views and are not reachable from the mobile app.
    /// </summary>
    private static int LevelForRole(int roleId) => roleId switch
    {
        116 => 5,
        125 => 6,
        130 => 1,
        _ => 5
    };

    public async Task<IReadOnlyList<ApplicationDto>> FetchAsync(
        FetchApplicationsRequest request, long officerId, int roleId, CancellationToken ct)
    {
        var level = LevelForRole(roleId);

        var p = new DynamicParameters();
        p.Add("@p_Level", level, DbType.Int32);
        p.Add("@p_Role", roleId, DbType.Int32);
        // Explicit size on every string. Unsized, Dapper sends nvarchar(4000) and SQL Server
        // wraps the column in CONVERT_IMPLICIT, which discards the index seek on EPID.
        p.Add("@p_Epid",
            string.IsNullOrWhiteSpace(request.Epid) ? null : request.Epid.Trim(),
            DbType.String, size: 50);
        p.Add("@p_Corp", request.CorporationId, DbType.Int32);
        p.Add("@p_Zone", request.ZoneId, DbType.Int32);
        p.Add("@p_Ward", request.WardId, DbType.Int32);
        p.Add("@p_ArchId", officerId, DbType.Int32);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var rows = (await connection.QueryAsync<ApplicationRow>(
            Sp.Call(procedures.Value.FetchApplications, p, ct, timeoutSeconds: 120))).AsList();

        logger.LogInformation(
            "Fetched {Count} applications for officer {OfficerId} role {RoleId} (level {Level}) "
            + "corp {Corp} zone {Zone} ward {Ward}",
            rows.Count, officerId, roleId, level, request.CorporationId, request.ZoneId, request.WardId);

        // Merge in who currently holds each record. The fetch procedure does not report it,
        // and without it the allot-to-me screen cannot tell a free record from a taken one.
        var assignments = await ActiveAssignmentsAsync(
            connection, rows.Select(r => (int)r.App_ID).Distinct().ToList(), ct);

        return rows.Select(r => Map(r, assignments, officerId)).ToList();
    }


    public async Task<AssignOutcome> AssignAsync(
        int appId, long officerId, int roleId, string? officerName, bool assign, CancellationToken ct)
    {
        var p = new DynamicParameters();
        p.Add("@AppId", appId, DbType.Int32);
        p.Add("@Arch_Id", officerId, DbType.Int32);
        p.Add("@Arch_Role", roleId, DbType.Int32);
        p.Add("@Arch_Name", officerName ?? "", DbType.String, size: 150);
        p.Add("@Status", "Pending", DbType.String, size: 30);
        // IsActive is the assign/unassign switch: 1 inserts a new holding, 0 releases the
        // officer's existing Pending row.
        p.Add("@IsActive", assign, DbType.Boolean);

        await using var connection = await connections.OpenAsync(DbTarget.B2A, ct);

        var row = await connection.QueryFirstOrDefaultAsync<AssignResultRow>(
            Sp.Call(procedures.Value.AssignApplication, p, ct));

        if (row is null)
            throw new InvalidOperationException(
                "The assignment procedure returned no row, which it is not expected to do.");

        logger.LogInformation(
            "Assign appId={AppId} officer={OfficerId} assign={Assign} -> {StatusCode} {Message}",
            appId, officerId, assign, row.StatusCode, row.Message);

        return new AssignOutcome(row.StatusCode, row.Assign_Id, row.App_id, row.Message);
    }

    /// <summary>
    /// Active assignments for a page of applications, so the list can show what is already
    /// taken. One query for the whole page - never one per row.
    /// </summary>
    private async Task<Dictionary<int, AssignmentRow>> ActiveAssignmentsAsync(
        SqlConnection connection, IReadOnlyCollection<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0) return [];

        const string sql = """
            SELECT AppId, Arch_Id, Arch_Name, Status, AssignmentDate
            FROM dbo.BtoA_Architect_AssignedApp WITH (NOLOCK)
            WHERE IsActive = 1 AND AppId IN @appIds;
            """;

        var rows = await connection.QueryAsync<AssignmentRow>(
            new CommandDefinition(sql, new { appIds }, cancellationToken: ct));

        // An application should only have one active holding, but the table does not enforce
        // that, so keep the most recent rather than throwing on a duplicate.
        return rows
            .GroupBy(r => r.AppId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AssignmentDate).First());
    }
    private static ApplicationDto Map(
        ApplicationRow row, IReadOnlyDictionary<int, AssignmentRow> assignments, long officerId)
    {
        assignments.TryGetValue((int)row.App_ID, out var held);

        return new ApplicationDto
        {
        AppId = row.App_ID,
        ApplicationId = row.ApplicationDisplayId,
        Epid = row.PropertyId,
        SasId = row.SasNo,
        OwnerNames = row.OwnerNames,
        // The column is a naive datetime. Stamp IST explicitly rather than letting the
        // server's local zone decide - these dates are read on a phone in another process.
        DateOfApplied = row.SubmittedOn is { } d
            ? new DateTimeOffset(d, TimeSpan.FromHours(5.5))
            : null,
        Status = row.Status,
        ApplicationType = row.Application_Type,
        RoadType = row.RoadType,
        IsObjected = row.isObjectedFlag,
        HasGuidanceValue = row.GVFlag,
        QueueNo = row.QueueNo,
            AssignedToUserId = held?.Arch_Id,
            AssignedToName = held?.Arch_Name,
            AssignmentStatus = held?.Status,
            AssignedOn = held?.AssignmentDate is { } a
                ? new DateTimeOffset(a, TimeSpan.FromHours(5.5))
                : null,
            IsAssignedToMe = held is not null && held.Arch_Id == officerId,
            IsAssignedToOther = held is not null && held.Arch_Id != officerId
        };
    }
}
