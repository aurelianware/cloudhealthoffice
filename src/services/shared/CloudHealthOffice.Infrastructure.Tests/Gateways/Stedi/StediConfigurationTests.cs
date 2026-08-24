using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;

/// <summary>
/// Covers Stedi configuration validation and gateway registration/resolution
/// (task sections 4, 15, 17): actionable config errors, Stedi resolvable when
/// selected, and Mock remaining the resolved gateway when Stedi is not configured.
/// </summary>
public class StediConfigurationTests
{
    [Fact]
    public void Validate_MissingApiKey_IsReported()
    {
        var errors = new StediGatewayOptions { ApiKey = null, BaseUrl = "https://x.test", Environment = "sandbox" }.Validate();
        errors.Should().Contain(e => e.Contains("ApiKey"));
    }

    [Fact]
    public void Validate_MissingBaseUrl_IsReported()
    {
        var errors = new StediGatewayOptions { ApiKey = "k", BaseUrl = "", Environment = "sandbox" }.Validate();
        errors.Should().Contain(e => e.Contains("BaseUrl"));
    }

    [Fact]
    public void Validate_InvalidEnvironment_IsReported()
    {
        var errors = new StediGatewayOptions { ApiKey = "k", BaseUrl = "https://x.test", Environment = "staging" }.Validate();
        errors.Should().Contain(e => e.Contains("Environment"));
    }

    [Fact]
    public void Validate_ValidConfiguration_HasNoErrors()
    {
        var errors = new StediGatewayOptions
        {
            ApiKey = "k", BaseUrl = "https://healthcare.us.stedi.com", Environment = "production"
        }.Validate();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ApiKey_IsNeverIncludedInValidationMessages()
    {
        var errors = new StediGatewayOptions
        {
            ApiKey = "SUPER-SECRET-KEY", BaseUrl = "", Environment = "nope"
        }.Validate();
        string.Join(" ", errors).Should().NotContain("SUPER-SECRET-KEY");
    }

    [Fact]
    public void DefaultGatewayStedi_WithConfig_ResolvesStediEligibilityGateway()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["HealthcareTransactions:DefaultGateway"] = "Stedi",
            ["HealthcareTransactions:Gateways:Stedi:ApiKey"] = "test-key",
            ["HealthcareTransactions:Gateways:Stedi:BaseUrl"] = "https://healthcare.us.stedi.com",
            ["HealthcareTransactions:Gateways:Stedi:Environment"] = "sandbox"
        });

        var resolver = provider.GetRequiredService<IHealthcareGatewayResolver>();

        var gateway = resolver.Resolve();
        gateway.Name.Should().Be(StediHealthcareGateway.GatewayName);
        resolver.ResolveCapability<IEligibilityGateway>().Should().BeAssignableTo<IEligibilityGateway>();
        resolver.ResolveCapability<IClaimAcknowledgmentGateway>().Should().BeAssignableTo<IClaimAcknowledgmentGateway>();
        gateway.Supports(GatewayCapability.ClaimAcknowledgment).Should().BeTrue();
    }

    [Fact]
    public void DefaultGatewayStedi_ButNoStediConfig_StillResolvesStedi_NotMock()
    {
        // No Stedi section, but Stedi is the default: it must still be registered
        // so that invoking it surfaces a Configuration error rather than silently
        // falling back to Mock.
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["HealthcareTransactions:DefaultGateway"] = "Stedi"
        });

        var resolver = provider.GetRequiredService<IHealthcareGatewayResolver>();

        resolver.Resolve().Name.Should().Be(StediHealthcareGateway.GatewayName);
    }

    [Fact]
    public void MockSelected_WithNoStediCredentials_ResolvesMock_AndStediNotRegistered()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["HealthcareTransactions:DefaultGateway"] = "Mock"
        });

        var resolver = provider.GetRequiredService<IHealthcareGatewayResolver>();

        resolver.Resolve().Name.Should().Be(MockHealthcareGateway.GatewayName);
        var act = () => resolver.Resolve("Stedi");
        act.Should().Throw<InvalidOperationException>();
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddChoHealthcareGateways(config);
        return services.BuildServiceProvider();
    }
}
