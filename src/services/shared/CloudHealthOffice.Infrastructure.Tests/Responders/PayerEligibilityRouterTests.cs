using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilityRouterTests
{
    private readonly PayerEligibilityRouter _router = new(new InMemoryPayerEligibilityDirectory());

    [Fact]
    public void ExternalPayerId_ResolvesDemoTenant()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry
        {
            PayerId = ChoDemoEligibilitySeed.ExternalPayerId,
            ClaimedTenantId = "untrusted"
        });

        result.IsResolved.Should().BeTrue();
        result.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
        result.CanonicalPayerId.Should().Be(ChoDemoEligibilitySeed.CanonicalPayerId);
    }

    [Fact]
    public void TradingPartnerId_ResolvesDemoTenant()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry
        {
            TradingPartnerId = ChoDemoEligibilitySeed.TradingPartnerId
        });

        result.IsResolved.Should().BeTrue();
        result.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
    }

    [Fact]
    public void AuthenticatedEndpoint_WinsOverPayerId()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry
        {
            AuthenticatedEndpointId = ChoDemoEligibilitySeed.AuthenticatedEndpointId,
            PayerId = "unknown-payer"
        });

        result.IsResolved.Should().BeTrue();
        result.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
    }

    [Fact]
    public void ClaimedTenantId_IsIgnored()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry
        {
            PayerId = ChoDemoEligibilitySeed.ExternalPayerId,
            ClaimedTenantId = ChoDemoEligibilitySeed.OtherTenantId
        });

        result.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
        result.TenantId.Should().NotBe(ChoDemoEligibilitySeed.OtherTenantId);
    }

    [Fact]
    public void UnknownPayer_FailsExplicitly()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry { PayerId = "NO-SUCH-PAYER" });

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(EligibilityBusinessStatus.InvalidPayer);
        result.TenantId.Should().BeNull();
    }

    [Fact]
    public void AmbiguousPayer_FailsExplicitly()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry
        {
            PayerId = ChoDemoEligibilitySeed.AmbiguousExternalId
        });

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(EligibilityBusinessStatus.AmbiguousPayer);
        result.TenantId.Should().BeNull();
    }

    [Fact]
    public void MissingPayerIdentifier_Fails()
    {
        var result = _router.Resolve(new PayerEligibilityInquiry());

        result.IsResolved.Should().BeFalse();
        result.Status.Should().Be(EligibilityBusinessStatus.InvalidPayer);
    }
}
