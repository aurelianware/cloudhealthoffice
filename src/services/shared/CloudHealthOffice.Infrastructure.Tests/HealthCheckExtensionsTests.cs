using CloudHealthOffice.Infrastructure.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests;

public class HealthCheckExtensionsTests
{
    [Fact]
    public void AddChoHealthChecks_WithNoOptions_RegistersSelfCheck()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks();

        var provider = services.BuildServiceProvider();
        var healthCheckOptions = provider.GetService<IOptions<HealthCheckServiceOptions>>();
        healthCheckOptions.Should().NotBeNull();

        var registrations = healthCheckOptions!.Value.Registrations;
        registrations.Should().Contain(r => r.Name == "self");
    }

    [Fact]
    public void AddChoHealthChecks_SelfCheck_HasLiveTag()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks();

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var selfCheck = registrations.First(r => r.Name == "self");
        selfCheck.Tags.Should().Contain("live");
    }

    [Fact]
    public void AddChoHealthChecks_WithMongoDb_RegistersMongoCheck()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks(opts =>
        {
            opts.MongoDbConnectionString = "mongodb://localhost:27017";
        });

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "mongodb");
        var mongoCheck = registrations.First(r => r.Name == "mongodb");
        mongoCheck.Tags.Should().Contain("ready");
        mongoCheck.Tags.Should().Contain("db");
    }

    [Fact]
    public void AddChoHealthChecks_WithCosmosDbConnectionString_RegistersCosmosCheck()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks(opts =>
        {
            opts.CosmosDbConnectionString = "AccountEndpoint=https://test.documents.azure.com:443/;AccountKey=dGVzdA==;";
        });

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "cosmosdb");
        var cosmosCheck = registrations.First(r => r.Name == "cosmosdb");
        cosmosCheck.Tags.Should().Contain("ready");
        cosmosCheck.Tags.Should().Contain("db");
    }

    [Fact]
    public void AddChoHealthChecks_WithCosmosDbEndpointAndKey_RegistersCosmosCheck()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks(opts =>
        {
            opts.CosmosDbEndpoint = "https://test.documents.azure.com:443/";
            opts.CosmosDbKey = "dGVzdA==";
        });

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "cosmosdb");
    }

    [Fact]
    public void AddChoHealthChecks_WithRedis_RegistersRedisCheck()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks(opts =>
        {
            opts.RedisConnectionString = "localhost:6379";
        });

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "redis");
        var redisCheck = registrations.First(r => r.Name == "redis");
        redisCheck.Tags.Should().Contain("ready");
        redisCheck.Tags.Should().Contain("cache");
        redisCheck.FailureStatus.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public void AddChoHealthChecks_WithHttpDependencies_RegistersDependencyChecks()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks(opts =>
        {
            opts.HttpDependencies = new Dictionary<string, string>
            {
                ["auth-service"] = "https://auth.example.com/health",
                ["member-service"] = "https://members.example.com/health"
            };
        });

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().Contain(r => r.Name == "auth-service");
        registrations.Should().Contain(r => r.Name == "member-service");

        var authCheck = registrations.First(r => r.Name == "auth-service");
        authCheck.Tags.Should().Contain("ready");
        authCheck.Tags.Should().Contain("dependency");
        authCheck.FailureStatus.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public void AddChoHealthChecks_WithNoDatabase_DoesNotRegisterDbChecks()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks();

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        registrations.Should().NotContain(r => r.Name == "mongodb");
        registrations.Should().NotContain(r => r.Name == "cosmosdb");
        registrations.Should().NotContain(r => r.Name == "redis");
    }

    [Fact]
    public void AddChoHealthChecks_CosmosDbConnectionString_TakesPrecedence_OverEndpointKey()
    {
        var services = new ServiceCollection();

        services.AddChoHealthChecks(opts =>
        {
            opts.CosmosDbConnectionString = "AccountEndpoint=https://test.documents.azure.com:443/;AccountKey=dGVzdA==;";
            opts.CosmosDbEndpoint = "https://other.documents.azure.com:443/";
            opts.CosmosDbKey = "b3RoZXI=";
        });

        var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        // Should only register one cosmosdb check, not two
        registrations.Where(r => r.Name == "cosmosdb").Should().HaveCount(1);
    }

    [Fact]
    public void ChoHealthCheckOptions_HttpDependencies_DefaultsToEmpty()
    {
        var options = new ChoHealthCheckOptions();

        options.HttpDependencies.Should().NotBeNull();
        options.HttpDependencies.Should().BeEmpty();
    }

    [Fact]
    public void ChoHealthCheckOptions_AllPropertiesDefaultToNull()
    {
        var options = new ChoHealthCheckOptions();

        options.MongoDbConnectionString.Should().BeNull();
        options.CosmosDbConnectionString.Should().BeNull();
        options.CosmosDbEndpoint.Should().BeNull();
        options.CosmosDbKey.Should().BeNull();
        options.RedisConnectionString.Should().BeNull();
    }
}
