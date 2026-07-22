using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Behavior coverage for <see cref="ProviderIntegrityStage"/> — the
/// federal-exclusion check that closes the gap left by capability 5.5's
/// original six-stage scope (see the stage's own doc comment).
/// </summary>
public class ProviderIntegrityStageTests
{
    private const string TenantId = "tenant-a";
    private const string BillingNpi = "1234567890";
    private const string RenderingNpi = "9000000011";

    private readonly IProviderIntegrityClient _client = Substitute.For<IProviderIntegrityClient>();

    private ProviderIntegrityStage NewStage() =>
        new(_client, NullLogger<ProviderIntegrityStage>.Instance);

    [Fact]
    public async Task NoProviderNpiOnClaim_returns_Pass_without_calling_client()
    {
        var claim = NewClaim(billingNpi: null, renderingNpi: null);
        var ctx = NewContext(claim);
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _client.DidNotReceiveWithAnyArgs().CheckAsync(default!, default!, default);
    }

    [Fact]
    public async Task ClearProvider_returns_Pass()
    {
        _client.CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegritySnapshot { Passed = true });

        var ctx = NewContext(NewClaim(BillingNpi, null));
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
    }

    [Fact]
    public async Task ExcludedProvider_returns_Deny_and_sets_denial_reason()
    {
        _client.CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegritySnapshot
            {
                Passed = false,
                IsExcluded = true,
                DenialCode = "B7",
                DenialReason = "Provider is excluded from federal healthcare programs",
            });

        var ctx = NewContext(NewClaim(BillingNpi, null));
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue, "Deny short-circuits the remaining non-persistence stages");
        Assert.Equal("B7", ctx.AdjudicationResult.DenialReasonCode);
        Assert.Equal(
            "Provider is excluded from federal healthcare programs",
            ctx.AdjudicationResult.DenialReason);
    }

    [Fact]
    public async Task RequiresManualReview_returns_Pend_with_MEDREVIEW_code()
    {
        _client.CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegritySnapshot
            {
                Passed = false,
                IsExcluded = false,
                RequiresManualReview = true,
                DenialReason = "Provider verification could not reach a confident determination; manual review required",
            });

        var ctx = NewContext(NewClaim(BillingNpi, null));
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue, "Pend is recoverable — pipeline continues");
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal(ProviderIntegrityStage.MedicalReviewPendCode, ctx.PendDetails!.PendCode);
        Assert.Equal("MEDREVIEW", ctx.PendDetails.PendCode);
    }

    [Fact]
    public async Task ClientReturnsNull_TransportFailureReachingBenefitPlanService_returns_Pend_not_silent_pass()
    {
        // The gate's own "never fail open" contract covers failures
        // reaching ITS upstreams. If benefit-plan-service itself can't be
        // reached at all, the stage must apply the same policy rather than
        // silently passing the claim.
        _client.CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>())
            .Returns((ProviderIntegritySnapshot?)null);

        var ctx = NewContext(NewClaim(BillingNpi, null));
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(ProviderIntegrityStage.MedicalReviewPendCode, ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task RenderingProviderExcluded_denies_even_when_billing_provider_clear()
    {
        _client.CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegritySnapshot { Passed = true });
        _client.CheckAsync(TenantId, RenderingNpi, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegritySnapshot
            {
                Passed = false,
                IsExcluded = true,
                DenialCode = "B7",
                DenialReason = "Provider is excluded from federal healthcare programs",
            });

        var ctx = NewContext(NewClaim(BillingNpi, RenderingNpi));
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        await _client.Received(1).CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>());
        await _client.Received(1).CheckAsync(TenantId, RenderingNpi, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SameBillingAndRenderingNpi_checks_only_once()
    {
        _client.CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>())
            .Returns(new ProviderIntegritySnapshot { Passed = true });

        var ctx = NewContext(NewClaim(BillingNpi, BillingNpi));
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _client.Received(1).CheckAsync(TenantId, BillingNpi, Arg.Any<CancellationToken>());
    }

    private static AdapterClaim NewClaim(string? billingNpi, string? renderingNpi) => new()
    {
        TenantId = TenantId,
        Id = "claim-1",
        ClaimNumber = "C-1",
        ClaimVersionId = "v-1",
        BillingProviderNPI = billingNpi ?? string.Empty,
        RenderingProviderNPI = renderingNpi ?? string.Empty,
        ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        BenefitPlanId = "plan-1",
    };

    private static ClaimAdjudicationContext NewContext(AdapterClaim claim) => new()
    {
        TenantId = TenantId,
        ClaimVersionId = claim.ClaimVersionId,
        Claim = claim,
    };
}
