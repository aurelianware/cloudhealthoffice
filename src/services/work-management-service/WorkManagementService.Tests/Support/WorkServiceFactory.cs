using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkManagementService.Repositories;
using WorkManagementService.Services;

namespace WorkManagementService.Tests.Support;

/// <summary>
/// The real work-management-service pipeline (CHO authentication, tenant from
/// the token, permission policies, controllers) over an in-memory store and a
/// scripted claims-service. Background maintenance is off.
/// </summary>
public sealed class WorkServiceFactory : WebApplicationFactory<Program>
{
    public InMemoryWorkStore Store { get; } = new();
    public FakeClaimsWorkSource Claims { get; } = new();
    public WorkSeedTracker Seeds { get; } = new();
    public InMemoryWorkspaceStore Workspace { get; } = new();

    public void Reset()
    {
        Store.Clear();
        Workspace.Clear();
        Seeds.Clear();
        Claims.Pended.Clear();
        Claims.Fail = false;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        foreach (var (key, value) in ChoDevelopmentAuth.Configuration())
            builder.UseSetting(key, value);
        builder.UseSetting("WorkManagement:Reconcile:Enabled", "false");
        builder.UseSetting("WorkManagement:RescoreIntervalMinutes", "0");
        builder.UseSetting("Kafka:BootstrapServers", "");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWorkStore>();
            services.AddSingleton<IWorkStore>(Store);
            services.RemoveAll<IWorkspaceStore>();
            services.AddSingleton<IWorkspaceStore>(Workspace);
            services.RemoveAll<WorkSeedTracker>();
            services.AddSingleton(Seeds);
            services.RemoveAll<IClaimsWorkSource>();
            services.AddSingleton<IClaimsWorkSource>(Claims);
        });
    }

    /// <summary>A client acting as <paramref name="user"/> with one built-in role, in the test tenant.</summary>
    public HttpClient Client(string user, string role) => TenantClient(WorkTestKit.Tenant, user, role);

    public HttpClient TenantClient(string tenant, string user, string role)
    {
        var client = CreateDefaultClient(new ChoDevelopmentTokenHandler(user, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }
}

[CollectionDefinition(nameof(WorkServiceCollection))]
public sealed class WorkServiceCollection : ICollectionFixture<WorkServiceFactory>
{
}
