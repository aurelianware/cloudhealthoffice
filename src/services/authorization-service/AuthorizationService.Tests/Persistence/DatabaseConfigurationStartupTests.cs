using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthorizationService.Tests.Persistence;

/// <summary>
/// A deployed host with no database configured must refuse to start, not go
/// Ready and answer every request with a 500. Development hosts (tests, local
/// runs that substitute the repository) still start.
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
            .Where(e => e.ToString().Contains("No database is configured"));
    }

    [Fact]
    public void DevelopmentHost_WithNoDatabaseConfigured_Starts()
    {
        using var host = new NoDatabaseHost("Development");

        host.Services.GetRequiredService<IServiceProvider>().Should().NotBeNull();
    }

    private sealed class NoDatabaseHost(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            if (environment == "Development")
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
            builder.UseSetting("Database:Provider", "");
            builder.UseSetting("MongoDb:ConnectionString", "");
            builder.UseSetting("CosmosDb:Endpoint", "");
            builder.UseSetting("CosmosDb:ConnectionString", "");
        }
    }
}
