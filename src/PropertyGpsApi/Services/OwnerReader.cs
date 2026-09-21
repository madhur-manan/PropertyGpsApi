using Dapper;
using Microsoft.Data.SqlClient;

using PropertyGpsApi.Models;

using PropertyGpsApi.Interfaces;

namespace PropertyGpsApi.Services;


/// <summary>
/// Who owns each property.
///
/// USP_S_GetAppDetails does not return owners, but the officer's list shows them - it is
/// how an RI recognises the property they are standing in front of, since an EPID is not
/// something anyone recognises on sight. Without this the list renders a column of blanks.
///
/// Names are Kannada nvarchar; nothing here transliterates or truncates them.
/// </summary>
internal sealed class OwnerReader : IOwnerReader
{
    private const string Sql = """
        SELECT Own_App_Id AS AppId, Own_OwnerName AS OwnerName, Own_Mobile AS Mobile
        FROM dbo.BtoA_OwnerDetails WITH (NOLOCK)
        WHERE ISNULL(own_active, 1) = 1 AND Own_App_Id IN @appIds;
        """;

    public async Task<IReadOnlyDictionary<int, OwnerSummary>> ForAsync(
        SqlConnection connection, IReadOnlyCollection<int> appIds, CancellationToken ct)
    {
        if (appIds.Count == 0) return new Dictionary<int, OwnerSummary>();

        // One query for the whole page, never one per property.
        var rows = await connection.QueryAsync<OwnerRow>(
            new CommandDefinition(Sql, new { appIds }, commandTimeout: 60, cancellationToken: ct));

        return rows
            .GroupBy(r => r.AppId)
            .ToDictionary(
                g => g.Key,
                g => new OwnerSummary(
                    Join(g.Select(r => r.OwnerName)),
                    Join(g.Select(r => r.Mobile))));
    }

    /// <summary>
    /// A property can have several owners, and the app expects one free-text field rather
    /// than a list. Blanks are dropped so a missing mobile does not leave a stray comma.
    /// </summary>
    private static string Join(IEnumerable<string?> values) =>
        string.Join(", ", values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct());

    private sealed class OwnerRow
    {
        public int AppId { get; init; }
        public string? OwnerName { get; init; }
        public string? Mobile { get; init; }
    }
}
