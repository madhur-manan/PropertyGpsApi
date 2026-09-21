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
    private const string Sql = """
        SELECT AppId, Arch_Id, Arch_Name, Status, AssignmentDate
        FROM dbo.BtoA_Architect_AssignedApp WITH (NOLOCK)
        WHERE IsActive = 1 AND AppId IN @appIds;
        """;

    public async Task<IReadOnlyDictionary<int, AssignmentRow>> ActiveForAsync(
        SqlConnection connection, IReadOnlyCollection<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0) return new Dictionary<int, AssignmentRow>();

        // One query for the whole page, never one per row.
        var rows = await connection.QueryAsync<AssignmentRow>(
            new CommandDefinition(Sql, new { appIds }, cancellationToken: ct));

        // An application should only have one active holding, but the table does not enforce
        // it, so keep the most recent rather than throwing on a duplicate.
        return rows
            .GroupBy(r => r.AppId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AssignmentDate).First());
    }
}
