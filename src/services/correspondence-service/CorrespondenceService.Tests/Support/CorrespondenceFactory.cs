using CorrespondenceService.Repositories;
using CorrespondenceService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CorrespondenceService.Tests.Support;

/// <summary>The real pipeline (auth, tenant middleware, controllers) over in-memory storage.</summary>
public sealed class CorrespondenceFactory : WebApplicationFactory<Program>
{
    /// <summary>A MongoDB address that is never listening; the repository is swapped out.</summary>
    public const string UnreachableMongo = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=100&connectTimeoutMS=100";

    public InMemoryCorrespondenceRepository Repo { get; } = new();
    public FakeLetterDocumentStore Documents { get; } = new();

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
