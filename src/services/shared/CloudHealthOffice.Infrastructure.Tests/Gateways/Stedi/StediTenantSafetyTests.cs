using System.Net;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Tenant-safety: overlays and deprecated maps are scoped to the requesting
/// tenant, and one tenant cannot resolve another tenant's identifiers.
/// </summary>
public class StediTenantSafetyTests
{
    private static StediGatewayOptions OptionsWithMaps()
    {
        var o = new StediGatewayOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox"
        };
        o.PayerMap["UNKNOWN-PAYER"] = "999";
        o.TenantPayerMap["tenant-alpha"] = new Dictionary<string, string> { ["AETNA"] = "111" };
        return o;
    }

    [Fact]
    public async Task PayerResolver_UsesTenantSpecificDeprecatedMap_OverDirectory()
    {
        var resolver = PayerTestHarness.CreateResolver(Options.Create(OptionsWithMaps()));

        var result = await resolver.ResolveAsync("tenant-alpha", "AETNA", CancellationToken.None);

        result.Status.Should().Be(PayerResolutionStatus.Found);
        result.ExternalIdentifierValue.Should().Be("111");
        result.UsedDeprecatedFallback.Should().BeTrue();
    }

    [Fact]
    public async Task PayerResolver_OtherTenant_CannotSeeAnotherTenantsMapping()
    {
        var resolver = PayerTestHarness.CreateResolver(Options.Create(OptionsWithMaps()));

        var result = await resolver.ResolveAsync("tenant-beta", "AETNA", CancellationToken.None);

        result.Status.Should().Be(PayerResolutionStatus.Found);
        result.ExternalIdentifierValue.Should().Be(SyntheticPayerSeed.EligibleTradingPartnerId);
        result.ExternalIdentifierValue.Should().NotBe("111");
    }

    [Fact]
    public async Task PayerResolver_TenantMapping_IsCaseInsensitive()
    {
        var resolver = PayerTestHarness.CreateResolver(Options.Create(OptionsWithMaps()));

        var result = await resolver.ResolveAsync("tenant-alpha", "aetna", CancellationToken.None);

        result.ExternalIdentifierValue.Should().Be("111");
    }

    [Fact]
    public async Task PayerResolver_UnknownPayer_DoesNotPassThrough()
    {
        var resolver = PayerTestHarness.CreateResolver(Options.Create(OptionsWithMaps()));

        var result = await resolver.ResolveAsync("tenant-beta", "CIGNA-DIRECT", CancellationToken.None);

        result.Status.Should().Be(PayerResolutionStatus.PayerNotFound);
        result.ExternalIdentifierValue.Should().BeNull();
    }

    [Fact]
    public async Task PayerResolver_DeprecatedGlobalMap_IsFallbackWhenDirectoryMisses()
    {
        var resolver = PayerTestHarness.CreateResolver(Options.Create(OptionsWithMaps()));

        var result = await resolver.ResolveAsync("tenant-beta", "UNKNOWN-PAYER", CancellationToken.None);

        result.Status.Should().Be(PayerResolutionStatus.Found);
        result.ExternalIdentifierValue.Should().Be("999");
        result.UsedDeprecatedFallback.Should().BeTrue();
    }

    [Fact]
    public async Task PayerResolver_NullPayer_ReturnsNotFound()
    {
        var resolver = PayerTestHarness.CreateResolver(Options.Create(OptionsWithMaps()));

        var result = await resolver.ResolveAsync("tenant-alpha", null, CancellationToken.None);

        result.Status.Should().Be(PayerResolutionStatus.PayerNotFound);
    }

    [Fact]
    public async Task TenantOverride_IsIsolatedPerTenant()
    {
        var store = PayerTestHarness.CreateStore();
        var service = PayerTestHarness.CreateService(store);
        await service.SaveTenantOverrideAsync(new PayerTenantOverride
        {
            TenantId = "tenant-alpha",
            PayerId = SyntheticPayerSeed.EligibleId,
            Enabled = true,
            ExternalIdentifiers =
            {
                new PayerExternalIdentifier
                {
                    System = StediPayerIdentifiers.System,
                    Type = StediPayerIdentifiers.TradingPartnerServiceIdType,
                    Value = "OVERRIDE-ALPHA"
                }
            }
        });

        var alpha = await service.ResolveForTransactionAsync(
            "tenant-alpha",
            SyntheticPayerSeed.EligibleId,
            CloudHealthOffice.Infrastructure.Gateways.HealthcareTransactionType.Eligibility270271,
            StediPayerIdentifiers.System,
            StediPayerIdentifiers.TradingPartnerServiceIdType);

        var beta = await service.ResolveForTransactionAsync(
            "tenant-beta",
            SyntheticPayerSeed.EligibleId,
            CloudHealthOffice.Infrastructure.Gateways.HealthcareTransactionType.Eligibility270271,
            StediPayerIdentifiers.System,
            StediPayerIdentifiers.TradingPartnerServiceIdType);

        alpha.ExternalIdentifierValue.Should().Be("OVERRIDE-ALPHA");
        beta.ExternalIdentifierValue.Should().Be(SyntheticPayerSeed.EligibleTradingPartnerId);

        var leaked = await service.GetTenantOverrideAsync("tenant-beta", SyntheticPayerSeed.EligibleId);
        leaked.Should().BeNull();
    }

    [Fact]
    public async Task TenantContext_SurvivesTheTransaction()
    {
        const string activeJson = "{\"planStatus\":[{\"statusCode\":\"1\"}]}";
        var options = Options.Create(OptionsWithMaps());
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, activeJson);
        var apiClient = new StediEligibilityApiClient(
            new StubHttpClientFactory(handler), options,
            NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var gateway = new StediHealthcareGateway(
            apiClient, PayerTestHarness.CreateResolver(options), options,
            NullLogger<StediHealthcareGateway>.Instance);

        var response = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "M1",
            ProviderNpi = "1",
            PayerId = "AETNA"
        });

        response.Metadata.TenantId.Should().Be("tenant-alpha");
        handler.RequestBodies[0].Should().Contain("111");
    }
}
