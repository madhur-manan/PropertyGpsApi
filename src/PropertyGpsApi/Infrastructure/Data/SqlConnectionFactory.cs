using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PropertyGpsApi.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace PropertyGpsApi.Infrastructure.Data;

/// <summary>
/// The data genuinely lives in two databases: officer/master data in masterDB, and the
/// B2A application data in KhataBtoA. Naming them separately lets a DBA grant two
/// least-privilege logins and gives each its own connection pool.
/// </summary>
public enum DbTarget
{
    Master,
    B2A
}

public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(DbTarget target, CancellationToken ct = default);
}

internal sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly Dictionary<DbTarget, string> _connectionStrings;

    public SqlConnectionFactory(IOptions<DatabaseOptions> options)
    {
        var db = options.Value;
        _connectionStrings = new Dictionary<DbTarget, string>
        {
            [DbTarget.Master] = Decorate(db.Master, "PropertyGpsApi"),
            [DbTarget.B2A] = Decorate(db.B2A, "PropertyGpsApi")
        };
    }

    private static string Decorate(string connectionString, string applicationName) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            // Not cosmetic: this is how BBMP's DBA tells our sessions apart in
            // sys.dm_exec_sessions when something misbehaves on a shared server.
            ApplicationName = applicationName,
            ConnectTimeout = 15,
            ConnectRetryCount = 3,
            ConnectRetryInterval = 5,
            MultipleActiveResultSets = false
        }.ConnectionString;

    public async Task<SqlConnection> OpenAsync(DbTarget target, CancellationToken ct = default)
    {
        var connection = new SqlConnection(_connectionStrings[target]);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public static class SqlTransience
{
    /// <summary>
    /// Error numbers worth retrying. 1205 (deadlock victim) is included deliberately: the
    /// legacy procedures mix NOLOCK and HOLDLOCK, so deadlocks are a realistic outcome
    /// rather than a sign of a bug.
    /// </summary>
    private static readonly HashSet<int> Retryable =
    [
        -2, 20, 64, 121, 233, 1205, 1231, 4060, 4221,
        10053, 10054, 10060, 10928, 10929, 11001,
        40197, 40501, 40613, 49918, 49919, 49920
    ];

    public static bool IsTransient(SqlException exception) =>
        exception.Errors.Cast<SqlError>().Any(e => Retryable.Contains(e.Number));
}

public static class Sp
{
    /// <summary>
    /// Dapper's Query*Async(string, object) overloads accept no CancellationToken.
    /// CommandDefinition is the only way to pass one - without it, a client that walks out
    /// of coverage keeps a worker and a pooled connection busy until the command timeout.
    /// </summary>
    public static CommandDefinition Call(
        string name, object? parameters, CancellationToken ct, int timeoutSeconds = 30) =>
        new(name, parameters,
            commandType: CommandType.StoredProcedure,
            commandTimeout: timeoutSeconds,
            cancellationToken: ct);
}
