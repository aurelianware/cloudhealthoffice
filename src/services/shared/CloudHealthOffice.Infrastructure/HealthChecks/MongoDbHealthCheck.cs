using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.HealthChecks;

public class MongoDbHealthCheck : IHealthCheck
{
    private readonly string _connectionString;

    public MongoDbHealthCheck(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = new MongoClient(_connectionString);
            var db = client.GetDatabase("admin");
            await db.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB is reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB is unreachable", ex);
        }
    }
}
