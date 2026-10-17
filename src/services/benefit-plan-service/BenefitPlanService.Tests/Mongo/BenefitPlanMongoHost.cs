using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BenefitPlanService.Tests.Mongo;

/// <summary>
/// The real benefit-plan-service host (its own <c>Program</c>) against a real
/// mongod (EphemeralMongo), so every repository comes out of the service
/// collection the host builds, over the <c>IMongoDatabase</c> the host itself
/// registers.
///
/// <para>
/// Hosted services are removed (they would reach Redis, tenant-service and
/// the claims-service); the PA-rule seeder's registration is kept aside as
/// <see cref="PaRuleSeederType"/> so a test can run it on demand.
/// </para>
/// </summary>
internal sealed class BenefitPlanMongoHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _databaseName;

    public BenefitPlanMongoHost(string connectionString, string databaseName)
    {
        _connectionString = connectionString;
        _databaseName = databaseName;
    }

    /// <summary>The PA-rule engine's startup seeder, as the host registered it.</summary>
    public Type? PaRuleSeederType { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Program.cs reads these while building, so they go in as host settings.
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration("benefit-plan-service"))
        {
            ["MongoDb:ConnectionString"] = _connectionString,
            ["MongoDb:DatabaseName"] = _databaseName,
            // Nothing listens here; the multiplexer is only built, never needed.
            // The PA-rule cache decorator's SCAN flush on delete would time out and
            // throw (RedisTimeoutException is not a RedisException, D-34), so tests
            // that delete PA rules use PaRuleRepositoryMongo directly.
            ["Redis:ConnectionString"] = "localhost:1,abortConnect=false,connectTimeout=100",
        };
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            var seeder = services.FirstOrDefault(d =>
                d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.Name == "PriorAuthRuleEngineSeeder");
            PaRuleSeederType = seeder?.ImplementationType;
            services.RemoveAll<IHostedService>();
        });
    }
}
