using Dapper;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Models;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// Who currently holds each application. The fetch procedure does not report it, and
/// without it the allot-to-me screen cannot tell a free record from a taken one.
/// </summary>
internal sealed class AssignmentReader : IAssignmentReader
{
    /// <summary>
    /// The ids go in as one comma-separated string, not as "IN @appIds": that expands to one
    /// parameter per id, and the ward sync now asks about every application a phone holds -
    /// a large ward would pass SQL Server's 2100-parameter limit.
    /// </summary>
    internal const string Sql = """
        SELECT a.AppId, a.Arch_Id, a.Arch_Name, a.Status, a.AssignmentDate
        FROM dbo.BtoA_Architect_AssignedApp a WITH (NOLOCK)
        JOIN (SELECT DISTINCT AppId = TRY_CAST(value AS int) FROM STRING_SPLIT(@appIds, ',')) ids
          ON ids.AppId = a.AppId
        WHERE a.IsActive = 1
          AND NOT (a.Status = 'Completed' AND EXISTS (
                SELECT 1 FROM dbo.BtoAMainApp m WITH (NOLOCK)
                 WHERE m.App_Id = a.AppId AND m.App_Status = 400));
        """;

    // The NOT above: QC returned it, so the earlier officer's completed row no longer holds
    // it - any RI of the ward may take it (N2). ApplicationService.ReleaseReturnedSql retires
    // that row when someone does.

    public async Task<IReadOnlyDictionary<int, AssignmentRow>> ActiveForAsync(SqlConnection connection, IReadOnlyCollection<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0) return new Dictionary<int, AssignmentRow>();

        // One query for the whole page, never one per row.
        var rows = await connection.QueryAsync<AssignmentRow>(
            new CommandDefinition(Sql, new { appIds = string.Join(',', appIds.Distinct()) }, cancellationToken: ct));

        // An application should only have one active holding, but the table does not enforce
        // it, so keep the most recent rather than throwing on a duplicate.
        return rows
            .GroupBy(r => r.AppId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AssignmentDate).First());
    }
}
