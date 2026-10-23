using CorrespondenceService.Repositories;
using CorrespondenceService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using CloudHealthOffice.PlanConfiguration;

namespace CorrespondenceService.Tests.Support;

/// <summary>The real pipeline (auth, tenant middleware, controllers) over in-memory storage.</summary>
public sealed class CorrespondenceFactory : WebApplicationFactory<Program>
{
    /// <summary>A MongoDB address that is never listening; the repository is swapped out.</summary>
    public const string UnreachableMongo = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100";

    public InMemoryCorrespondenceRepository Repo { get; } = new();
    public FakeLetterDocumentStore Documents { get; } = new();

    /// <summary>
    /// The tenant's carriers. Empty by default, so letters compose with no
    /// sender, which is what every existing template expects. The host's real
    /// directory would reach tenant-service and the read is deliberately
    /// fail-closed (plan-configuration.md §12.4), so leaving it in place would
    /// make every compose a 500. A test that wants a letterhead adds one.
    /// </summary>
    public InMemoryCarrierDirectory Carriers { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MongoDb:ConnectionString"] = UnreachableMongo,
        }));
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Correspondence:RequireSeparateApprover"] = "true",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICorrespondenceRepository>();
            services.AddSingleton<ICorrespondenceRepository>(Repo);
            services.RemoveAll<ILetterDocumentStore>();
            services.AddSingleton<ILetterDocumentStore>(Documents);
            services.RemoveAll<ICarrierDirectory>();
            services.AddSingleton<ICarrierDirectory>(Carriers);
        });
        return base.CreateHost(builder);
    }
}

internal static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
    }
}
