using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// A host with no database configured must refuse to start, not go Ready and
/// answer every request with a 500. There is no "no database" mode: this holds
/// in every environment, Development and Testing included.
/// </summary>
public sealed class DatabaseConfigurationStartupTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DeployedHost_WithNoDatabaseConfigured_FailsAtStartup(string environment)
    {
        using var host = new NoDatabaseHost(environment);

        var act = () => host.Services.GetRequiredService<IServiceProvider>();

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.ToString().Contains(NoDatabaseMessage));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Host_ToldToUseTheCosmosNativeSdk_FailsAtStartup(string environment)
    {
        using var host = new NoDatabaseHost(environment, provider: "CosmosDb");

        var act = () => host.Services.GetRequiredService<IServiceProvider>();

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.ToString().Contains("Database:Provider 'CosmosDb' is not supported"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentHost_WithNoDatabaseConfigured_FailsAtStartup(string environment)
    {
        using var host = new NoDatabaseHost(environment);

        var act = () => host.Services.GetRequiredService<IServiceProvider>();

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.ToString().Contains(NoDatabaseMessage));
    }

    private const string NoDatabaseMessage =
        "Authorization Service requires a database. Configure MongoDb:ConnectionString.";

    private sealed class NoDatabaseHost(string environment, string provider = "") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            if (environment is "Development" or "Testing")
            {
                foreach (var (key, value) in ChoDevelopmentAuth.Configuration("authorization-service"))
                    builder.UseSetting(key, value);
            }
            else
            {
                // A deployed host refuses symmetric keys; an OIDC issuer is only
                // contacted when a token arrives, so none is needed to start.
                builder.UseSetting("ChoAuth:Audience", "cho-api");
                builder.UseSetting("ChoAuth:Issuers:0:Issuer", "https://issuer.example.test/");
                builder.UseSetting("ChoAuth:Issuers:0:Authority", "https://issuer.example.test/");
            }
            builder.UseSetting("Database:Provider", provider);
            builder.UseSetting("MongoDb:ConnectionString", "");
            if (provider.Length > 0)
            {
                // What AddChoDatabase needs to wire the Cosmos client; never contacted.
                builder.UseSetting("CosmosDb:Endpoint", "https://localhost:1/");
                builder.UseSetting("CosmosDb:Key", "a2V5");
            }
        }
    }
}
