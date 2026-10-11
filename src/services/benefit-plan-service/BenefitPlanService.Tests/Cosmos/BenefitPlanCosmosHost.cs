using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BenefitPlanService.Tests.Cosmos;

/// <summary>
/// The real benefit-plan-service host (its own <c>Program</c>) started in
/// Cosmos mode (<c>Database:Provider=CosmosDb</c>) against the emulator, so
/// every repository comes out of the service collection the host builds,
/// over the <see cref="CosmosClient"/> registration the host itself resolves.
///
/// <para>
/// One transport-only change: the emulator serves the gateway protocol only
/// (a Direct-mode client gets 400 on its address lookup), so the client the
/// host's registration builds is re-created with
/// <see cref="ConnectionMode.Gateway"/> on the same
/// <see cref="CosmosClientOptions"/> instance. Its serializer, and every other
/// option, is the host's. <see cref="HostSerializer"/> records which
/// serializer the host's registration chose.
/// </para>
///
/// <para>
/// Hosted services are removed (they would reach Redis, tenant-service and
/// the claims-service); the PA-rule seeder's registration is kept aside as
/// <see cref="PaRuleSeederType"/> so a test can run it on demand.
/// </para>
/// </summary>
internal sealed class BenefitPlanCosmosHost : WebApplicationFactory<Program>
{
    public const string BenefitPlansContainer = "BenefitPlans";

    private readonly string _databaseName;
    private readonly string _endpoint;

    public BenefitPlanCosmosHost(string databaseName, string? endpoint = null)
    {
        _databaseName = databaseName;
        _endpoint = endpoint ?? CosmosEmulator.Endpoint;
    }

    /// <summary>The serializer on the client the host's own registration built (null = the SDK's Newtonsoft default).</summary>
    public CosmosSerializer? HostSerializer { get; private set; }

    /// <summary>True once the host's client has been built.</summary>
    public bool HostClientBuilt { get; private set; }

    /// <summary>The PA-rule engine's startup seeder, as the host registered it.</summary>
    public Type? PaRuleSeederType { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Program.cs reads these while building, so they go in as host settings.
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration("benefit-plan-service"))
        {
            ["Database:Provider"] = "CosmosDb",
            ["MongoDb:ConnectionString"] = "",
            ["CosmosDb:Endpoint"] = _endpoint,
            ["CosmosDb:Key"] = CosmosEmulator.Key,
            ["CosmosDb:DatabaseName"] = _databaseName,
            ["CosmosDb:ContainerName"] = BenefitPlansContainer,
            // Nothing listens here: the PA-rule cache's state flush (the only
            // Redis use on these paths) logs the failure and carries on.
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

            // The registration the host resolves is the last one.
            var hostClient = services.Last(d => d.ServiceType == typeof(CosmosClient));
            services.RemoveAll<CosmosClient>();
            services.AddSingleton(sp => GatewayTransport(BuildHostClient(hostClient, sp)));
        });
    }

    private CosmosClient BuildHostClient(ServiceDescriptor descriptor, IServiceProvider sp)
    {
        var client = descriptor.ImplementationInstance as CosmosClient
                     ?? descriptor.ImplementationFactory?.Invoke(sp) as CosmosClient
                     ?? throw new InvalidOperationException("The host's CosmosClient registration is not a factory or instance.");
        HostSerializer = client.ClientOptions.Serializer;
        HostClientBuilt = true;
        return client;
    }

    private static CosmosClient GatewayTransport(CosmosClient hostClient)
    {
        var options = hostClient.ClientOptions;
        options.ConnectionMode = ConnectionMode.Gateway;
        options.LimitToEndpoint = true;
        options.RequestTimeout = TimeSpan.FromSeconds(30);
        options.MaxRetryAttemptsOnRateLimitedRequests = 20;
        var endpoint = hostClient.Endpoint.ToString();
        hostClient.Dispose();
        return new CosmosClient(endpoint, CosmosEmulator.Key, options);
    }
}
