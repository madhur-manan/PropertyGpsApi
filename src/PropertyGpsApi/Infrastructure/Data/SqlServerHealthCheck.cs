using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PropertyGpsApi.Infrastructure.Data;

/// <summary>
/// Round-trips both databases through the real connection factory, so a wrong connection
/// string or a missing grant is visible from /health/ready rather than from a log file
/// after an officer has already failed to sign in.
/// </summary>
public sealed class SqlServerHealthCheck(ISqlConnectionFactory connections) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        foreach (var target in new[] { DbTarget.Master, DbTarget.B2A })
        {
            try
            {
                await using var connection = await connections.OpenAsync(target, cancellationToken);
                await connection.ExecuteScalarAsync<int>(
                    new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy($"Cannot reach the {target} database.", ex);
            }
        }

        return HealthCheckResult.Healthy();
    }
}
