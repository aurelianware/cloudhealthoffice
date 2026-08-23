using System.Net;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Covers task section 9 and the tenant-safety acceptance criteria: payer
/// mapping is tenant-scoped, and the tenant context survives the transaction.
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
        o.PayerMap["AETNA"] = "999";
        o.TenantPayerMap["tenant-alpha"] = new Dictionary<string, string> { ["AETNA"] = "111" };
        return o;
    }

    [Fact]
    public void PayerResolver_UsesTenantSpecificMapping_First()
    {
        var resolver = new StediPayerResolver(Options.Create(OptionsWithMaps()));

        resolver.Resolve("tenant-alpha", "AETNA").Should().Be("111");
    }

    [Fact]
    public void PayerResolver_OtherTenant_CannotSeeAnotherTenantsMapping()
    {
        var resolver = new StediPayerResolver(Options.Create(OptionsWithMaps()));

        // tenant-beta has no map of its own, so it falls back to the global map
        // (999) and never resolves tenant-alpha's private value (111).
        resolver.Resolve("tenant-beta", "AETNA").Should().Be("999");
    }

    [Fact]
    public void PayerResolver_TenantMapping_IsCaseInsensitive()
    {
        var resolver = new StediPayerResolver(Options.Create(OptionsWithMaps()));

        // Tenant map key is "AETNA"; a differently-cased canonical id still resolves.
        resolver.Resolve("tenant-alpha", "aetna").Should().Be("111");
    }

    [Fact]
    public void PayerResolver_UnknownPayer_PassesThrough()
    {
        var resolver = new StediPayerResolver(Options.Create(OptionsWithMaps()));

        resolver.Resolve("tenant-beta", "CIGNA-DIRECT").Should().Be("CIGNA-DIRECT");
    }

    [Fact]
    public void PayerResolver_NullPayer_ReturnsNull()
    {
        var resolver = new StediPayerResolver(Options.Create(OptionsWithMaps()));

        resolver.Resolve("tenant-alpha", null).Should().BeNull();
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
            apiClient, new StediPayerResolver(options), options,
            NullLogger<StediHealthcareGateway>.Instance);

        var response = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "M1",
            ProviderNpi = "1",
            PayerId = "AETNA"
        });

        response.Metadata.TenantId.Should().Be("tenant-alpha");
        // The tenant-scoped payer mapping (111) was applied for this tenant.
        handler.RequestBodies[0].Should().Contain("111");
    }
}
