using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.6 — behavior coverage for
/// <see cref="NetworkCredentialingStage"/>: tier-walk, fail-mode policy
/// matrix, time-anchor resolution, and outcome combination.
/// </summary>
public class NetworkCredentialingStageTests
{
    private const string TenantId = "tenant-a";
    private const string Network1 = "net-1";
    private const string Network2 = "net-2";
    private const string Npi = "1234567890";
    private const string RenderingNpi = "9000000011";
    private const string ProviderId = "p-001";
    private const string RenderingProviderId = "p-rendering-excluded";

    private readonly IProviderMembershipClient _membership = Substitute.For<IProviderMembershipClient>();
    private readonly ICredentialingStatusClient _credentialing = Substitute.For<ICredentialingStatusClient>();

    private NetworkCredentialingStage NewStage(TenantEnforcementPolicyOptions opts) =>
        new(_membership, _credentialing, Options.Create(opts), NullLogger<NetworkCredentialingStage>.Instance);

    [Fact]
    public async Task BothChecksAllow_returns_Pass()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = ProviderId, AsOfDate = DateTime.UtcNow, Status = "Approved",
            });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.MatchedNetworkTier);
        Assert.Equal(Network1, ctx.MatchedNetworkTier!.NetworkId);
        Assert.Equal(2, ctx.EnforcementOutcomes.Count);
        Assert.All(ctx.EnforcementOutcomes, o => Assert.Equal(EnforcementDecision.Allow, o.Decision));
    }

    [Fact]
    public async Task StaleInactiveMembership_force_refreshes_before_denying()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = false, AsOfDate = DateTime.UtcNow,
                ParticipationStatus = "not_a_member",
            });
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), true, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = ProviderId, AsOfDate = DateTime.UtcNow, Status = "Approved",
            });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.All(ctx.EnforcementOutcomes, o => Assert.Equal(EnforcementDecision.Allow, o.Decision));
        await _membership.Received(1).GetMembershipAsync(
            TenantId, Network1, Npi, Arg.Any<DateTime>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleCredentialingStatus_force_refreshes_before_denying()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = ProviderId, AsOfDate = DateTime.UtcNow, Status = "Unknown",
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), true, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = ProviderId, AsOfDate = DateTime.UtcNow, Status = "Approved",
            });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.All(ctx.EnforcementOutcomes, o => Assert.Equal(EnforcementDecision.Allow, o.Decision));
        await _credentialing.Received(1).GetStatusAsOfAsync(
            TenantId, ProviderId, Arg.Any<DateTime>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenderingProviderCredentialingDenial_denies_even_when_billing_provider_allows()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = ProviderId, AsOfDate = DateTime.UtcNow, Status = "Approved",
            });
        _membership.GetMembershipAsync(TenantId, Network1, RenderingNpi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = RenderingNpi, ProviderId = RenderingProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, RenderingProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot
            {
                ProviderId = RenderingProviderId, AsOfDate = DateTime.UtcNow, Status = "Denied",
            });

        var claim = NewClaim();
        claim.RenderingProviderNPI = RenderingNpi;
        var ctx = NewContext(claim, tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal(4, ctx.EnforcementOutcomes.Count);
        Assert.NotNull(ctx.BillingProviderCredentialingStatus);
        Assert.NotNull(ctx.RenderingProviderCredentialingStatus);
        Assert.Equal("Denied", ctx.RenderingProviderCredentialingStatus!.Status);
        Assert.Contains(ctx.EnforcementOutcomes, outcome =>
            outcome.Check == EnforcementCheck.Credentialing
            && outcome.Decision == EnforcementDecision.Deny
            && (outcome.Reason?.Contains("Rendering provider", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task FirstTierMatches_skips_lower_priority_tiers()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot { ProviderId = ProviderId, Status = "Approved" });

        var ctx = NewContext(tiers: new[]
        {
            Tier(Network1, "InNetwork", 1),
            Tier(Network2, "Preferred", 2),
        });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        await sut.ExecuteAsync(ctx, CancellationToken.None);

        await _membership.Received(1).GetMembershipAsync(
            TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>());
        await _membership.DidNotReceive().GetMembershipAsync(
            TenantId, Network2, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoTierMatches_FailClosed_denies()
    {
        // Both tiers return non-active membership.
        _membership.GetMembershipAsync(TenantId, Arg.Any<string>(), Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var ctx = NewContext(tiers: new[]
        {
            Tier(Network1, "InNetwork", 1),
            Tier(Network2, "Preferred", 2),
        });
        var sut = NewStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.FailClosed,
        });

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Single(ctx.EnforcementOutcomes); // credentialing skipped for OON
        Assert.Equal(EnforcementDecision.Deny, ctx.EnforcementOutcomes[0].Decision);
    }

    [Fact]
    public async Task NoTierMatches_FailOpen_pends()
    {
        _membership.GetMembershipAsync(TenantId, Arg.Any<string>(), Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.FailOpen,
        });

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue); // pend doesn't short-circuit
        Assert.Equal(EnforcementDecision.Pend, ctx.EnforcementOutcomes[0].Decision);
    }

    [Fact]
    public async Task NoTierMatches_SoftValidation_passes()
    {
        _membership.GetMembershipAsync(TenantId, Arg.Any<string>(), Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.SoftValidation,
        });

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(EnforcementDecision.Observe, ctx.EnforcementOutcomes[0].Decision);
    }

    [Fact]
    public async Task DegradedMembership_FailClosed_denies()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((NetworkMembership?)null);

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.FailClosed,
        });

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Contains("membership-verification-unavailable",
            ctx.EnforcementOutcomes[0].Reason ?? string.Empty);
    }

    [Fact]
    public async Task ApprovedMembership_DegradedCredentialing_FailClosed_denies()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((CredentialingStatusSnapshot?)null);

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions
        {
            NetworkMode = NetworkEnforcementMode.FailClosed,
            CredentialingMode = CredentialingEnforcementMode.FailClosed,
        });

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        // Membership = Allow, Credentialing = Deny.
        Assert.Equal(EnforcementDecision.Allow, ctx.EnforcementOutcomes[0].Decision);
        Assert.Equal(EnforcementDecision.Deny, ctx.EnforcementOutcomes[1].Decision);
    }

    [Fact]
    public async Task ApprovedMembership_PendingCredentialing_FailClosed_denies()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network1, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot { ProviderId = ProviderId, Status = "Pending" });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
    }

    [Fact]
    public async Task EmptyTierList_denies()
    {
        var ctx = NewContext(tiers: Array.Empty<ResolvedNetworkTier>());
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        await _membership.DidNotReceive()
            .GetMembershipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TierWithoutNetworkId_is_skipped()
    {
        _membership.GetMembershipAsync(TenantId, Network2, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership
            {
                NetworkId = Network2, Npi = Npi, ProviderId = ProviderId,
                IsActiveMember = true, AsOfDate = DateTime.UtcNow,
            });
        _credentialing.GetStatusAsOfAsync(TenantId, ProviderId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new CredentialingStatusSnapshot { ProviderId = ProviderId, Status = "Approved" });

        var ctx = NewContext(tiers: new[]
        {
            // Legacy tier with NetworkId = null — must be skipped.
            Tier(networkId: null, "Legacy", 1),
            Tier(Network2, "Preferred", 2),
        });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        // Verify the legacy tier (NetworkId=null) was skipped.
        // NSubstitute requires matchers throughout when any arg uses one,
        // so the null check goes through Arg.Is<string?>.
        await _membership.DidNotReceive().GetMembershipAsync(
            Arg.Any<string>(), Arg.Is<string?>(n => n == null), Arg.Any<string>(),
            Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingBillingNpi_rejects()
    {
        var claim = NewClaim();
        claim.BillingProviderNPI = string.Empty;
        var ctx = NewContext(claim, tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.False(result.Continue);
    }

    [Fact]
    public void EarliestServiceDate_picks_min_across_header_and_lines()
    {
        var claim = NewClaim();
        claim.ServiceDateFrom = new DateTime(2025, 5, 10, 0, 0, 0, DateTimeKind.Utc);
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        claim.ClaimLines.Add(new AdapterClaimLine
        {
            ServiceDateFrom = new DateTime(2025, 5, 15, 0, 0, 0, DateTimeKind.Utc),
        });

        var earliest = NetworkCredentialingStage.ResolveEarliestServiceDate(claim);

        Assert.Equal(new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc), earliest);
    }

    [Fact]
    public async Task DenyMembership_short_circuits_credentialing_check()
    {
        _membership.GetMembershipAsync(TenantId, Network1, Npi, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new NetworkMembership { Npi = Npi, IsActiveMember = false });

        var ctx = NewContext(tiers: new[] { Tier(Network1, "InNetwork", 1) });
        var sut = NewStage(new TenantEnforcementPolicyOptions());

        await sut.ExecuteAsync(ctx, CancellationToken.None);

        // Credentialing only runs when membership Allows.
        await _credentialing.DidNotReceive().GetStatusAsOfAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static ResolvedNetworkTier Tier(string? networkId, string name, int level) => new()
    {
        TierName = name,
        TierLevel = level,
        NetworkId = networkId,
    };

    private static AdapterClaim NewClaim() => new()
    {
        TenantId = TenantId,
        Id = "claim-1",
        ClaimNumber = "C-1",
        ClaimVersionId = "v-1",
        BillingProviderNPI = Npi,
        ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BenefitPlanId = "plan-1",
    };

    private static ClaimAdjudicationContext NewContext(
        IReadOnlyList<ResolvedNetworkTier> tiers) => NewContext(NewClaim(), tiers);

    private static ClaimAdjudicationContext NewContext(
        AdapterClaim claim,
        IReadOnlyList<ResolvedNetworkTier> tiers) => new()
    {
        TenantId = TenantId,
        ClaimVersionId = claim.ClaimVersionId,
        Claim = claim,
        ResolvedPlan = new ResolvedBenefitPlan
        {
            Id = "plan-1",
            NetworkTiers = tiers,
        },
    };
}
