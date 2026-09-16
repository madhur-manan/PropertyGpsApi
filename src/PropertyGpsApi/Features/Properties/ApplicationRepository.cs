using System.Data;
using Dapper;
using Microsoft.Extensions.Options;
using PropertyGpsApi.Features.Properties.Dtos;
using PropertyGpsApi.Infrastructure.Data;
using PropertyGpsApi.Infrastructure.Options;

namespace PropertyGpsApi.Features.Properties;

public interface IApplicationRepository
{
    Task<AssignOutcome> AssignAsync(
        int appId, long officerId, int roleId, string? officerName, bool assign, CancellationToken ct);
}

/// <summary>
/// What USP_IU_Architect_AssignedApp reports back. StatusCode is 200 on success and 400 for
/// a business rejection - already assigned, or the officer's queue is full.
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
}
