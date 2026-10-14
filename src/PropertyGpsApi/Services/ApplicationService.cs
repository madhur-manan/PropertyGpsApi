using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Common;
using PropertyGpsApi.Models;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


internal sealed class ApplicationService(
    ISqlConnectionFactory connections,
    IOptions<StoredProcedureOptions> procedures,
    ILogger<ApplicationService> logger) : IApplicationService
{
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

        if (assign)
        {
            var released = await connection.ExecuteAsync(new CommandDefinition(
                ReleaseReturnedSql, new { appId, officerId, roleId }, commandTimeout: 30, cancellationToken: ct));
            if (released > 0)
                logger.LogInformation(
                    "Assign appId={AppId}: QC returned it, so the earlier officer's completed holding was released",
                    appId);
        }

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
    /// A survey QC has returned (App_Status 400) is open to any RI of the ward (N2). The officer
    /// who submitted it still has an active row - completed, not pending (db/19) - and
    /// USP_IU_Architect_AssignedApp refuses anyone while any active row exists. So that row is
    /// retired first: kept for the history, no longer a holding. Only for 400, and only a
    /// completed row; a pending holding is somebody's live work and is never touched.
    /// </summary>
    internal const string ReleaseReturnedSql = """
        UPDATE a
           SET IsActive = 0, Remarks = 'Returned by QC', UBy = @officerId, UDate = GETDATE(), URole = @roleId
          FROM dbo.BtoA_Architect_AssignedApp a
          JOIN dbo.BtoAMainApp m ON m.App_Id = a.AppId AND m.App_Active = 1
         WHERE a.AppId = @appId AND a.IsActive = 1 AND a.Status = 'Completed'
           AND m.App_Status = 400;
        """;
}
