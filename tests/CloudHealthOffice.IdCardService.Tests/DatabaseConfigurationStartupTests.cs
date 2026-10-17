using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.IdCardService.Tests;

/// <summary>
/// MongoDB is the only ID card store and there is no in-memory mode: a host
/// with no database configured refuses to start in every environment,
/// Development and Testing included.
/// </summary>
public sealed class DatabaseConfigurationStartupTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Production")]
    public void Host_WithNoDatabaseConfigured_FailsAtStartup(string environment)
    {
        using var host = new NoDatabaseHost(environment);

        var ex = Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<IServiceProvider>());

        Assert.Contains("ID Card Service requires a database. Configure MongoDb:ConnectionString.", ex.ToString());
    }

    private sealed class NoDatabaseHost(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            if (environment == "Production")
            {
                // A deployed host refuses symmetric keys; an OIDC issuer is only
                // contacted when a token arrives, so none is needed to start.
                builder.UseSetting("ChoAuth:Audience", "cho-api");
                builder.UseSetting("ChoAuth:Issuers:0:Issuer", "https://issuer.example.test/");
                builder.UseSetting("ChoAuth:Issuers:0:Authority", "https://issuer.example.test/");
            }
            else
            {
                foreach (var (key, value) in ChoDevelopmentAuth.Configuration("idcard-service"))
                    builder.UseSetting(key, value);
            }
            // Outside Development the provider scheme needs a real IdP; the
            // database check must be what stops the host, not this.
            builder.UseSetting("ProviderJwt:Authority", "https://provider-idp.invalid");
            builder.UseSetting("Messaging:Backend", "InMemory");
            builder.UseSetting("MongoDb:ConnectionString", "");
        }
    }
}
