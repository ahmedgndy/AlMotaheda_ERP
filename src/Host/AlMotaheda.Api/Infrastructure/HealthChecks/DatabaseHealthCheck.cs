using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AlMotaheda.Api.Infrastructure.HealthChecks;

/// <summary>
/// Readiness check for the shared SQL Server database: opens a connection and runs <c>SELECT 1</c>.
/// </summary>
/// <remarks>
/// Only on <c>/health</c>, not on <c>/health/live</c>: a database outage should take a server out of the load
/// balancer, not get the process restarted. Connections come from the SqlClient pool, so a check costs one round trip.
/// </remarks>
internal sealed class DatabaseHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public const string Name = "database";
    public const string ConnectionStringName = "Erp";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(configuration.GetConnectionString(ConnectionStringName));
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        await command.ExecuteScalarAsync(cancellationToken);

        return HealthCheckResult.Healthy();
    }
}
