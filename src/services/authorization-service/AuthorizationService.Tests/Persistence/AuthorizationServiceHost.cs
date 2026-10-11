using AuthorizationService.Repositories;
using AuthorizationService.Services;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Testing.Cosmos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// The real authorization-service host (its own <c>Program</c>) on a real
/// store: MongoDB (an EphemeralMongo mongod) or Cosmos DB (the emulator).
/// Every repository, the SLA watchdog and the <see cref="CosmosClient"/> come
/// out of the service collection the host builds, so a test exercises the
/// production registrations rather than hand-built objects.
///
/// <para>
/// Cosmos: one transport-only change. The emulator serves the gateway
/// protocol only, so the client the host's own registration builds is
/// re-created with <see cref="ConnectionMode.Gateway"/> on the same
/// <see cref="CosmosClientOptions"/> instance. Its serializer, and every other
/// option, is the host's. <see cref="HostSerializer"/> records which
/// serializer the host's registration chose.
/// </para>
///
/// <para>
/// Hosted services stay registered (the watchdog is resolved from them) but
/// none does work during a test: the watchdog's first tick is 15 minutes out,
/// the retention worker is disabled by default and the Kafka consumer is not
/// registered without <c>Kafka:BootstrapServers</c>.
/// </para>
/// </summary>
internal sealed class AuthorizationServiceHost : WebApplicationFactory<Program>
{
    public const string ContainerName = "Authorizations";

    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly bool _cosmos;

    private AuthorizationServiceHost(IReadOnlyDictionary<string, string?> settings, bool cosmos)
    {
        _settings = settings;
        _cosmos = cosmos;
    }

    /// <summary>MongoDB mode (the default provider) on <paramref name="connectionString"/>.</summary>
    public static AuthorizationServiceHost Mongo(string connectionString, string databaseName) =>
        new(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MongoDb",
            ["MongoDb:ConnectionString"] = connectionString,
            ["MongoDb:DatabaseName"] = databaseName,
        }, cosmos: false);

    /// <summary>Cosmos DB mode (<c>Database:Provider=CosmosDb</c>) on the emulator.</summary>
    public static AuthorizationServiceHost Cosmos(string databaseName, string? endpoint = null) =>
        new(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "CosmosDb",
            ["MongoDb:ConnectionString"] = "",
            ["CosmosDb:Endpoint"] = endpoint ?? CosmosEmulator.Endpoint,
            ["CosmosDb:Key"] = CosmosEmulator.Key,
            ["CosmosDb:DatabaseName"] = databaseName,
            ["CosmosDb:ContainerName"] = ContainerName,
        }, cosmos: true);

    /// <summary>The serializer on the client the host's own registration built (null = the SDK's Newtonsoft default).</summary>
    public CosmosSerializer? HostSerializer { get; private set; }

    /// <summary>True once the host's client has been built.</summary>
    public bool HostClientBuilt { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Program.cs reads these while building, so they go in as host settings.
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration("authorization-service"));
        foreach (var (key, value) in _settings)
            settings[key] = value;
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        if (!_cosmos)
            return;

        builder.ConfigureTestServices(services =>
        {
            // The registration the host resolves is the last one.
            var hostClient = services.Last(d => d.ServiceType == typeof(CosmosClient));
            services.RemoveAll<CosmosClient>();
            services.AddSingleton(sp => GatewayTransport(BuildHostClient(hostClient, sp)));
        });
    }

    /// <summary>The SLA watchdog exactly as the host registered it.</summary>
    public SlaWatchdogService Watchdog =>
        Services.GetServices<IHostedService>().OfType<SlaWatchdogService>().Single();

    /// <summary>
    /// The host's repository in a new scope with <paramref name="tenantId"/>
    /// as the request tenant, as <c>TenantMiddleware</c> would set it. Dispose
    /// the returned scope when done.
    /// </summary>
    public (IServiceScope Scope, IAuthorizationRepository Repository) RepositoryFor(string tenantId)
    {
        var scope = Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Items["TenantId"] = tenantId;
        accessor.HttpContext = context;
        return (scope, scope.ServiceProvider.GetRequiredService<IAuthorizationRepository>());
    }

    /// <summary>Clears the ambient request (the HttpContextAccessor holder is async-local).</summary>
    public void ClearRequestContext() =>
        Services.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

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
