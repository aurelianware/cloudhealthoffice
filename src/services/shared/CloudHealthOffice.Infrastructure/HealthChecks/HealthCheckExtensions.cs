using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.Json;

namespace CloudHealthOffice.Infrastructure.HealthChecks;

public static class HealthCheckExtensions
{
    /// <summary>
    /// Registers Cloud Health Office standard health checks: liveness, readiness with optional MongoDB, Redis, and HTTP dependency checks.
    /// </summary>
    public static IHealthChecksBuilder AddChoHealthChecks(this IServiceCollection services, Action<ChoHealthCheckOptions>? configure = null)
    {
        var options = new ChoHealthCheckOptions();
        configure?.Invoke(options);

        var builder = services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("Service is running"), tags: ["live"]);

        if (!string.IsNullOrEmpty(options.MongoDbConnectionString))
        {
            builder.AddCheck("mongodb",
                new MongoDbHealthCheck(options.MongoDbConnectionString),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "db"]);
        }

        if (!string.IsNullOrEmpty(options.CosmosDbConnectionString))
        {
            builder.AddCheck("cosmosdb",
                new CosmosDbHealthCheck(options.CosmosDbConnectionString),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "db"]);
        }
        else if (!string.IsNullOrEmpty(options.CosmosDbEndpoint) && !string.IsNullOrEmpty(options.CosmosDbKey))
        {
            builder.AddCheck("cosmosdb",
                new CosmosDbHealthCheck(options.CosmosDbEndpoint, options.CosmosDbKey),
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "db"]);
        }

        if (!string.IsNullOrEmpty(options.RedisConnectionString))
        {
            builder.AddCheck("redis",
                new RedisHealthCheck(options.RedisConnectionString),
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "cache"]);
        }

        foreach (var dep in options.HttpDependencies)
        {
            builder.AddCheck(dep.Key,
                new HttpDependencyHealthCheck(dep.Value),
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "dependency"]);
        }

        return builder;
    }

    /// <summary>
    /// Maps standard Cloud Health Office health check endpoints: /health (all), /health/live (liveness), /health/ready (readiness).
    /// </summary>
    public static IApplicationBuilder MapChoHealthChecks(this IApplicationBuilder app)
    {
        app.UseHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteHealthCheckResponse
        });

        app.UseHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = WriteHealthCheckResponse
        });

        app.UseHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = WriteHealthCheckResponse
        });

        return app;
    }

    private static async Task WriteHealthCheckResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var result = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds
            }),
            totalDuration = report.TotalDuration.TotalMilliseconds
        };

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

public class ChoHealthCheckOptions
{
    /// <summary>MongoDB connection string for DB health check. Leave null to skip.</summary>
    public string? MongoDbConnectionString { get; set; }

    /// <summary>Cosmos DB connection string for DB health check. Leave null to skip.</summary>
    public string? CosmosDbConnectionString { get; set; }

    /// <summary>Cosmos DB endpoint for DB health check (used with <see cref="CosmosDbKey"/>). Leave null to skip.</summary>
    public string? CosmosDbEndpoint { get; set; }

    /// <summary>Cosmos DB key for DB health check (used with <see cref="CosmosDbEndpoint"/>). Leave null to skip.</summary>
    public string? CosmosDbKey { get; set; }

    /// <summary>Redis connection string for cache health check. Leave null to skip.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Named HTTP dependency URLs to check (e.g., {"auth-service", "https://auth/health"}).</summary>
    public Dictionary<string, string> HttpDependencies { get; set; } = new();
}
