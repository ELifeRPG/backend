using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ELifeRPG.Api.Observability;

public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    private readonly string _connectionString = configuration.GetConnectionString("SharedDatabase")!;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Could not connect to Postgres.", exception);
        }
    }
}
