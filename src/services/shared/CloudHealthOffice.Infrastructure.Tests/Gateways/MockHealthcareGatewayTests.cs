using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

/// <summary>
/// Covers mock eligibility normalization (requirement 4) and tenant boundary
/// enforcement (requirement 5).
/// </summary>
public class MockHealthcareGatewayTests
{
    private static MockHealthcareGateway NewGateway() =>
        new(NullLogger<MockHealthcareGateway>.Instance);

    [Fact]
    public async Task SeededMember_ReturnsNormalizedActiveResponse()
    {
        var gateway = NewGateway();

        var result = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "SUB-1001",
            ProviderNpi = "1234567890"
        });

        result.IsSuccess.Should().BeTrue();
        result.Result.Should().NotBeNull();
        result.Result!.IsEligible.Should().BeTrue();
        result.Result.CoverageStatus.Should().Be(GatewayCoverageStatus.Active);
        result.Result.PlanId.Should().Be("ALPHA-PPO-GOLD");
        result.Result.Benefits.Should().ContainSingle()
            .Which.ServiceTypeCode.Should().Be("30");

        result.Metadata.TransactionType.Should().Be(HealthcareTransactionType.Eligibility270271);
        result.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        result.Metadata.GatewayName.Should().Be(MockHealthcareGateway.GatewayName);
    }

    [Fact]
    public async Task Response_IsDeterministic_ForRepeatedRequests()
    {
        var gateway = NewGateway();
        var request = new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "SUB-1001",
            ProviderNpi = "1234567890"
        };

        var first = await gateway.CheckEligibilityAsync(request);
        var second = await gateway.CheckEligibilityAsync(request);

        first.Result!.IsEligible.Should().Be(second.Result!.IsEligible);
        first.Result.PlanId.Should().Be(second.Result.PlanId);
        first.Result.StatusCode.Should().Be(second.Result.StatusCode);
    }

    [Fact]
    public async Task UnknownMember_ReturnsNormalizedInactiveResponse()
    {
        var gateway = NewGateway();

        var result = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "NOT-ON-FILE",
            ProviderNpi = "1234567890"
        });

        result.IsSuccess.Should().BeTrue();
        result.Result!.IsEligible.Should().BeFalse();
        result.Result.CoverageStatus.Should().Be(GatewayCoverageStatus.Inactive);
        result.Result.RejectionReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Request_MissingTenant_IsRejected()
    {
        var gateway = NewGateway();

        var result = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "",
            SubscriberId = "SUB-1001",
            ProviderNpi = "1234567890"
        });

        result.IsSuccess.Should().BeFalse();
        result.Metadata.Status.Should().Be(GatewayTransactionStatus.Failed);
        result.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
    }

    [Fact]
    public async Task TenantBoundary_MemberNotVisibleUnderAnotherTenant()
    {
        var gateway = NewGateway();

        // SUB-1001 is seeded active under tenant-alpha only.
        var crossTenant = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-beta",
            SubscriberId = "SUB-1001",
            ProviderNpi = "1234567890"
        });

        crossTenant.Result!.IsEligible.Should().BeFalse();
        // The response must never carry tenant-alpha's plan across the boundary.
        crossTenant.Result.PlanId.Should().NotBe("ALPHA-PPO-GOLD");
        // Metadata always echoes the requesting tenant, never the seeded one.
        crossTenant.Metadata.TenantId.Should().Be("tenant-beta");
    }

    [Fact]
    public async Task Metadata_EchoesRequestTenantAndCorrelation()
    {
        var gateway = NewGateway();

        var result = await gateway.CheckEligibilityAsync(new GatewayEligibilityRequest
        {
            TenantId = "tenant-alpha",
            SubscriberId = "SUB-1001",
            ProviderNpi = "1234567890",
            CorrelationId = "corr-xyz"
        });

        result.Metadata.TenantId.Should().Be("tenant-alpha");
        result.Metadata.CorrelationId.Should().Be("corr-xyz");
        result.Metadata.ExternalTransactionId.Should().NotBeNullOrEmpty();
        result.Metadata.CompletedAtUtc.Should().NotBeNull();
    }
}
