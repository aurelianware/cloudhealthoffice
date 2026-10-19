using AuthorizationService.Repositories;
using AuthorizationService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// The real authorization-service host (its own <c>Program</c>) on a real
/// store: MongoDB (an EphemeralMongo mongod). Every repository and the SLA
/// watchdog come out of the service collection the host builds, so a test
/// exercises the production registrations rather than hand-built objects.
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

    private AuthorizationServiceHost(IReadOnlyDictionary<string, string?> settings) => _settings = settings;

    /// <summary>The host on <paramref name="connectionString"/>.</summary>
    public static AuthorizationServiceHost Mongo(string connectionString, string databaseName) =>
        new(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MongoDb",
            ["MongoDb:ConnectionString"] = connectionString,
            ["MongoDb:DatabaseName"] = databaseName,
        });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Program.cs reads these while building, so they go in as host settings.
        var settings = new Dictionary<string, string?>(ChoDevelopmentAuth.Configuration("authorization-service"));
        foreach (var (key, value) in _settings)
            settings[key] = value;
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
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
}
