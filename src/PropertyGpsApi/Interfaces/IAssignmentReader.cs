using Dapper;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Interfaces;

internal interface IAssignmentReader
{
    Task<IReadOnlyDictionary<int, AssignmentRow>> ActiveForAsync(
        SqlConnection connection, IReadOnlyCollection<int> appIds, CancellationToken ct);
}
