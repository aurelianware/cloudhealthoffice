using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Opt-in developer smoke test against the real Stedi sandbox (task section 19).
/// It is skipped unless <c>CHO_STEDI_LIVE_TESTS=true</c> and <c>STEDI_API_KEY</c>
/// are set, so normal CI never depends on Stedi availability or credentials.
///
/// Run locally with, e.g.:
/// <code>
/// export CHO_STEDI_LIVE_TESTS=true
/// export STEDI_API_KEY="&lt;sandbox key&gt;"
/// export STEDI_TEST_PAYER_ID="&lt;sandbox payer id&gt;"
/// export STEDI_TEST_MEMBER_ID="&lt;sandbox member id&gt;"
/// dotnet test --filter FullyQualifiedName~StediLiveSmokeTests
/// </code>
/// </summary>
public class StediLiveSmokeTests
{
    [SkippableFact]
    public async Task Sandbox_Eligibility_ReturnsNormalizedResponse()
    {
        Skip.IfNot(
            string.Equals(Environment.GetEnvironmentVariable("CHO_STEDI_LIVE_TESTS"), "true",
                StringComparison.OrdinalIgnoreCase),
            "Set CHO_STEDI_LIVE_TESTS=true to run the live Stedi smoke test.");

        var apiKey = Environment.GetEnvironmentVariable("STEDI_API_KEY")
            ?? Environment.GetEnvironmentVariable("HealthcareTransactions__Gateways__Stedi__ApiKey");
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "STEDI_API_KEY is not set.");

        var payerId = Environment.GetEnvironmentVariable("STEDI_TEST_PAYER_ID") ?? "00007";
        var memberId = Environment.GetEnvironmentVariable("STEDI_TEST_MEMBER_ID") ?? "0000000000";

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HealthcareTransactions:DefaultGateway"] = "Stedi",
            ["HealthcareTransactions:Gateways:Stedi:ApiKey"] = apiKey,
            ["HealthcareTransactions:Gateways:Stedi:BaseUrl"] = "https://healthcare.us.stedi.com",
            ["HealthcareTransactions:Gateways:Stedi:Environment"] = "sandbox",
            // Explicit test mapping — live smoke does not start hosted seed
            // and must not rely on implicit payer-id pass-through.
            [$"HealthcareTransactions:Gateways:Stedi:PayerMap:{payerId}"] = payerId
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoHealthcareGateways(config);
        await using var provider = services.BuildServiceProvider();

        var eligibility = provider.GetRequiredService<IHealthcareGatewayResolver>()
            .ResolveCapability<IEligibilityGateway>();

        var response = await eligibility.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "live-smoke",
            SubscriberId = memberId,
            ProviderNpi = "1999999984",
            ServiceTypeCode = "30",
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            PayerId = payerId,
            CorrelationId = Guid.NewGuid().ToString("N")
        });

        // We do not assert coverage (payer-dependent), only that the gateway
        // completed a real round-trip and produced canonical metadata.
        response.Should().NotBeNull();
        response.Metadata.GatewayName.Should().Be("Stedi");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.Eligibility270271);
        response.Metadata.ErrorCategory.Should().NotBe(GatewayErrorCategory.PayerNotFound);
        response.Metadata.ErrorCategory.Should().NotBe(GatewayErrorCategory.Configuration);
    }
}
