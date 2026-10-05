using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Security;

/// <summary>
/// trading-partner-service's cluster Service listens on port 80 (targetPort
/// 8080). payment-service's deployed configuration (appsettings.json, no
/// Development overrides) must address that port, on a host that receives the
/// run's service token.
/// </summary>
public class TradingPartnerBaseUrlTests
{
    private sealed class DeployedConfigHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Not Development: appsettings.Development.json (localhost) is not loaded.
            builder.UseEnvironment("Testing");
            builder.UseSetting("PaymentRuns:ReservationReconciliationEnabled", "false");
            // Read while Program builds, so as host settings (not a later configuration source).
            foreach (var (key, value) in ChoDevelopmentAuth.Configuration("payment-service"))
                builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(ChoDevelopmentAuth.Configuration("payment-service")));
        }
    }

    [Fact]
    public void DeployedConfiguration_AddressesTheServicePort_OnAChoHost()
    {
        using var host = new DeployedConfigHost();

        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient(TradingPartnersClient.HttpClientName);

        client.BaseAddress!.Port.Should().Be(80, "the Service is port 80; 8080 is only the container port");
        client.BaseAddress.Host.Should().Be("trading-partner-service.cloudhealthoffice");
        host.Services.GetRequiredService<ChoOutboundHosts>().IsChoService(client.BaseAddress).Should().BeTrue();
    }

    [Fact]
    public void CodeDefault_AddressesTheServicePort()
    {
        new Uri(TradingPartnersClient.DefaultBaseUrl).Port.Should().Be(80);
    }
}
