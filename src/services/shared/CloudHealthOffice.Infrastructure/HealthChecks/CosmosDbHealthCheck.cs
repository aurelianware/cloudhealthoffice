using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudHealthOffice.Infrastructure.HealthChecks;

public class CosmosDbHealthCheck : IHealthCheck
{
    private readonly string _connectionString;
    private readonly string? _endpoint;
    private readonly string? _key;

    public CosmosDbHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public CosmosDbHealthCheck(string endpoint, string key)
    {
        _endpoint = endpoint;
        _key = key;
        _connectionString = null!;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            CosmosClient client;
            if (!string.IsNullOrEmpty(_connectionString))
            {
                client = new CosmosClient(_connectionString);
            }
            else
            {
                client = new CosmosClient(_endpoint, _key);
            }

            using (client)
            {
                await client.ReadAccountAsync();
            }

            return HealthCheckResult.Healthy("Cosmos DB is reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cosmos DB is unreachable", ex);
        }
    }
}
