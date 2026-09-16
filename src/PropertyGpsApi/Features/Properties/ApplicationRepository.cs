using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Properties;

public interface IApplicationRepository
{
    Task<IReadOnlyList<ApplicationDto>> FetchAsync(
        FetchApplicationsRequest request, long officerId, int roleId, CancellationToken ct);
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

        return rows.Select(Map).ToList();
    }

    private static ApplicationDto Map(ApplicationRow row) => new()
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
        QueueNo = row.QueueNo
    };
}
