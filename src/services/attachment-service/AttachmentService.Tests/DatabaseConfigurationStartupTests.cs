using AttachmentService.Controllers;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AttachmentService.Tests;

/// <summary>
/// MongoDB is the only attachment store: a host with no database configured
/// refuses to start in every environment, Development and Testing included,
/// rather than starting and failing on the first request.
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

        var act = () => host.Services.GetRequiredService<IServiceProvider>();

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.ToString().Contains(
                "Attachment Service requires a database. Configure MongoDb:ConnectionString."));
    }

    private sealed class NoDatabaseHost(string environment) : WebApplicationFactory<AttachmentsController>
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
                foreach (var (key, value) in ChoDevelopmentAuth.Configuration("attachment-service"))
                    builder.UseSetting(key, value);
            }
            builder.UseSetting("MongoDb:ConnectionString", "");
        }
    }
}
