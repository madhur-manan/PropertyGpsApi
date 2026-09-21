using Dapper;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Models;

namespace PropertyGpsApi.Interfaces;

internal interface IOwnerReader
{
    Task<IReadOnlyDictionary<int, OwnerSummary>> ForAsync(
        SqlConnection connection, IReadOnlyCollection<int> appIds, CancellationToken ct);
}
