using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ReferenceDataService.Controllers;
using Xunit;

namespace CloudHealthOffice.ReferenceDataService.Tests;

/// <summary>
/// Compliance configuration lives in MongoDB and there is no in-memory mode: a
/// host with no MongoDB connection string refuses to start in every
/// environment, Development and Testing included. (PostgreSQL, the store for
/// reference codes, is configured here so it is not what stops the host.)
/// </summary>
public sealed class DatabaseConfigurationStartupTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Production")]
    public void Host_WithNoMongoDatabaseConfigured_FailsAtStartup(string environment)
    {
        using var host = new NoMongoHost(environment);

        var act = () => host.Services.GetRequiredService<IServiceProvider>();

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.ToString().Contains(
                "Reference Data Service requires a database. Configure MongoDb:ConnectionString."));
    }

    private sealed class NoMongoHost(string environment) : WebApplicationFactory<ComplianceConfigController>
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
                foreach (var (key, value) in ChoDevelopmentAuth.Configuration("reference-data-service"))
                    builder.UseSetting(key, value);
            }
            builder.UseSetting("ConnectionStrings:PostgreSQL", "Host=unused.invalid;Database=unused;Username=unused;Password=unused");
            builder.UseSetting("ReferenceData:ApplySchemaMigrationsOnStartup", "false");
            builder.UseSetting("MongoDb:ConnectionString", "");
            builder.UseSetting("Database:Provider", "");
        }
    }
}
