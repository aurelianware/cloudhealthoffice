using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

/// <summary>
/// Covers gateway resolution (requirement 1), eligibility capability discovery
/// (requirement 2), and explicit rejection of unsupported capabilities
/// (requirement 3).
/// </summary>
public class HealthcareGatewayResolverTests
{
    [Fact]
    public void AddChoHealthcareGateways_ResolvesDefaultMockGateway()
    {
        var resolver = BuildResolver();

        var gateway = resolver.Resolve();

        gateway.Name.Should().Be(MockHealthcareGateway.GatewayName);
    }

    [Fact]
    public void Resolve_ByExplicitName_IsCaseInsensitive()
    {
        var resolver = BuildResolver();

        resolver.Resolve("mock").Name.Should().Be(MockHealthcareGateway.GatewayName);
    }

    [Fact]
    public void Resolve_UnknownGateway_Throws()
    {
        var resolver = BuildResolver();

        var act = () => resolver.Resolve("does-not-exist");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*does-not-exist*");
    }

    [Fact]
    public void EligibilityCapability_IsDiscoverable()
    {
        var resolver = BuildResolver();

        var gateway = resolver.Resolve();

        gateway.Supports(GatewayCapability.Eligibility).Should().BeTrue();
        gateway.Capabilities.Should().Contain(GatewayCapability.Eligibility);

        // And the capability-typed resolution returns a usable gateway.
        var eligibility = resolver.ResolveCapability<IEligibilityGateway>();
        eligibility.Should().BeAssignableTo<IEligibilityGateway>();
    }

    [Fact]
    public void ClaimSubmissionCapability_IsDiscoverable()
    {
        var resolver = BuildResolver();
        var gateway = resolver.Resolve();

        gateway.Supports(GatewayCapability.ClaimSubmission).Should().BeTrue();
        resolver.ResolveCapability<IClaimSubmissionGateway>().Should().BeAssignableTo<IClaimSubmissionGateway>();
    }

    [Fact]
    public void ClaimAttachmentCapability_IsDiscoverable()
    {
        var resolver = BuildResolver();
        var gateway = resolver.Resolve();

        gateway.Supports(GatewayCapability.ClaimAttachment).Should().BeTrue();
        resolver.ResolveCapability<IClaimAttachmentGateway>().Should().BeAssignableTo<IClaimAttachmentGateway>();
    }

    [Fact]
    public void ClaimStatusCapability_IsDiscoverable()
    {
        var resolver = BuildResolver();
        var gateway = resolver.Resolve();

        gateway.Supports(GatewayCapability.ClaimStatus).Should().BeTrue();
        resolver.ResolveCapability<IClaimStatusGateway>().Should().BeAssignableTo<IClaimStatusGateway>();
    }

    [Theory]
    [InlineData(GatewayCapability.ClaimAcknowledgment)]
    [InlineData(GatewayCapability.Remittance)]
    public void UnsupportedCapabilities_AreNotAdvertised(GatewayCapability capability)
    {
        var gateway = BuildResolver().Resolve();

        gateway.Supports(capability).Should().BeFalse();
    }

    [Fact]
    public void ResolveCapability_ForUnsupportedTransaction_ThrowsExplicitly()
    {
        var resolver = BuildResolver();

        var act = () => resolver.ResolveCapability<IRemittanceGateway>();

        act.Should().Throw<GatewayCapabilityNotSupportedException>()
            .Which.Capability.Should().Be(GatewayCapability.Remittance);
    }

    [Fact]
    public void ResolveCapability_UnsupportedTransaction_NamesTheGateway()
    {
        var resolver = BuildResolver();

        var act = () => resolver.ResolveCapability<IRemittanceGateway>();

        act.Should().Throw<GatewayCapabilityNotSupportedException>()
            .Which.GatewayName.Should().Be(MockHealthcareGateway.GatewayName);
    }

    private static IHealthcareGatewayResolver BuildResolver()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HealthcareTransactions:DefaultGateway"] = "Mock"
            })
            .Build();

        services.AddChoHealthcareGateways(config);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHealthcareGatewayResolver>();
    }
}
