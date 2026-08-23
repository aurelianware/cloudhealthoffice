using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Opt-in developer smoke tests against the real Stedi sandbox. Skipped unless
/// <c>CHO_STEDI_LIVE_TESTS=true</c> and an API key are set, so CI never depends
/// on Stedi credentials.
///
/// <code>
/// export CHO_STEDI_LIVE_TESTS=true
/// export STEDI_API_KEY="&lt;sandbox key&gt;"
/// # or HealthcareTransactions__Gateways__Stedi__ApiKey
/// dotnet test --filter FullyQualifiedName~StediLiveSmokeTests
/// </code>
/// </summary>
public class StediLiveSmokeTests
{
    private const string DocumentedTradingPartnerId = "87726";

    [SkippableFact]
    public async Task Sandbox_Eligibility_ReturnsNormalizedResponse()
    {
        var eligibility = CreateLiveGateway(out var apiKey);
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "STEDI_API_KEY is not set.");

        var payerId = Environment.GetEnvironmentVariable("STEDI_TEST_PAYER_ID") ?? "00007";
        var memberId = Environment.GetEnvironmentVariable("STEDI_TEST_MEMBER_ID") ?? "0000000000";

        var response = await eligibility!.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "live-smoke",
            SubscriberId = memberId,
            ProviderNpi = "1999999984",
            ServiceTypeCode = "30",
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
            PayerId = payerId,
            CorrelationId = Guid.NewGuid().ToString("N")
        });

        response.Should().NotBeNull();
        response.Metadata.GatewayName.Should().Be("Stedi");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.Eligibility270271);
        response.Metadata.ErrorCategory.Should().NotBe(GatewayErrorCategory.PayerNotFound);
        response.Metadata.ErrorCategory.Should().NotBe(GatewayErrorCategory.Configuration);
    }

    /// <summary>
    /// Documented Stedi UHC sandbox dependent happy path (John/Jane Doe).
    /// The synthetic fixture is encoded in the test; only the API key comes
    /// from the environment.
    /// </summary>
    [SkippableFact]
    public async Task Sandbox_DependentEligibility_ReturnsActiveCoverage()
    {
        var eligibility = CreateLiveGateway(out var apiKey, DocumentedTradingPartnerId);
        Skip.If(string.IsNullOrWhiteSpace(apiKey), "STEDI_API_KEY is not set.");

        var response = await eligibility!.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "live-smoke",
            SubscriberId = "UHC202649",
            SubscriberFirstName = "John",
            SubscriberLastName = "Doe",
            ProviderNpi = "1999999984",
            ProviderOrganizationName = "Provider Name",
            ServiceTypeCode = "30",
            PayerId = DocumentedTradingPartnerId,
            Patient = new GatewayEligibilityPerson
            {
                FirstName = "Jane",
                LastName = "Doe",
                DateOfBirth = new DateOnly(1952, 11, 21)
            },
            CorrelationId = Guid.NewGuid().ToString("N")
        });

        response.IsSuccess.Should().BeTrue();
        response.Metadata.GatewayName.Should().Be("Stedi");
        response.Metadata.TransactionType.Should().Be(HealthcareTransactionType.Eligibility270271);
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        response.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
        response.Result.Should().NotBeNull();
        response.Result!.IsEligible.Should().BeTrue();
        response.Result.CoverageStatus.Should().Be(GatewayCoverageStatus.Active);
        response.Result.PlanId.Should().NotBeNullOrWhiteSpace();
        response.Result.Benefits.Should().NotBeEmpty();
        response.Result.Subscriber!.MemberId.Should().Be("UHC202649");
        response.Result.Patient!.FirstName.Should().Be("Jane");
    }

    private static IEligibilityGateway? CreateLiveGateway(out string? apiKey, string? extraPayerMapId = null)
    {
        apiKey = null;
        if (!string.Equals(Environment.GetEnvironmentVariable("CHO_STEDI_LIVE_TESTS"), "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true, "Set CHO_STEDI_LIVE_TESTS=true to run the live Stedi smoke test.");
            return null;
        }

        apiKey = Environment.GetEnvironmentVariable("STEDI_API_KEY")
            ?? Environment.GetEnvironmentVariable("HealthcareTransactions__Gateways__Stedi__ApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var settings = new Dictionary<string, string?>
        {
            ["HealthcareTransactions:DefaultGateway"] = "Stedi",
            ["HealthcareTransactions:Gateways:Stedi:ApiKey"] = apiKey,
            ["HealthcareTransactions:Gateways:Stedi:BaseUrl"] = "https://healthcare.us.stedi.com",
            ["HealthcareTransactions:Gateways:Stedi:Environment"] = "sandbox"
        };

        var payerId = extraPayerMapId
            ?? Environment.GetEnvironmentVariable("STEDI_TEST_PAYER_ID")
            ?? "00007";
        settings[$"HealthcareTransactions:Gateways:Stedi:PayerMap:{payerId}"] = payerId;
        if (extraPayerMapId is null)
        {
            settings[$"HealthcareTransactions:Gateways:Stedi:PayerMap:{DocumentedTradingPartnerId}"] =
                DocumentedTradingPartnerId;
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChoHealthcareGateways(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHealthcareGatewayResolver>()
            .ResolveCapability<IEligibilityGateway>();
    }
}
