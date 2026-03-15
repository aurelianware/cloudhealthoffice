using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudHealthOffice.Infrastructure.HealthChecks;

public class RedisHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public RedisHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Basic TCP connectivity check to Redis without requiring StackExchange.Redis dependency
            var parts = _connectionString.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 6379;

            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken);
            return HealthCheckResult.Healthy("Redis is reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Redis is unreachable", ex);
        }
    }
}
