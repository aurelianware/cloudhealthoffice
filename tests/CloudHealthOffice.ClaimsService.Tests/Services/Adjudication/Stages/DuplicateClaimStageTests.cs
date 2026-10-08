using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Behavior coverage for <see cref="DuplicateClaimStage"/> — exact
/// duplicates deny with CARC 18, suspect duplicates pend, and replacements,
/// voids, the claim itself and its own version chain are never flagged.
/// </summary>
public class DuplicateClaimStageTests
{
    private const string TenantId = "tenant-a";
    private const string MemberId = "M-100";
    private const string BillingNpi = "1234567890";
    private const string OtherNpi = "9000000011";

    private static readonly DateTime Dos = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PriorSubmitted = new(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime IncomingSubmitted = new(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly IClaimRepository _repository = Substitute.For<IClaimRepository>();

    private DuplicateClaimStage NewStage(
        DuplicateClaimEnforcementMode exact = DuplicateClaimEnforcementMode.Deny,
        DuplicateClaimEnforcementMode suspect = DuplicateClaimEnforcementMode.PendForReview) =>
        new(_repository,
            Options.Create(new TenantEnforcementPolicyOptions
            {
                ExactDuplicateMode = exact,
                SuspectDuplicateMode = suspect,
            }),
            NullLogger<DuplicateClaimStage>.Instance);

    private void PriorClaimsAre(params Claim[] priors) =>
        _repository
            .FindDuplicateCandidatesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(priors);

    [Fact]
    public void Defaults_are_exact_Deny_and_suspect_Pend()
    {
        var options = new TenantEnforcementPolicyOptions();

        Assert.Equal(DuplicateClaimEnforcementMode.Deny, options.ExactDuplicateMode);
        Assert.Equal(DuplicateClaimEnforcementMode.PendForReview, options.SuspectDuplicateMode);
        Assert.Equal(120, NewStage().Order);
        Assert.False(NewStage().IsRequired);
    }

    [Fact]
    public async Task ExactDuplicate_is_denied_with_CARC_18_group_CO()
    {
        PriorClaimsAre(PriorClaim());
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue, "Deny short-circuits the remaining non-persistence stages");
        Assert.Equal("18", ctx.AdjudicationResult.DenialReasonCode);
        Assert.Contains("CN-PRIOR", ctx.AdjudicationResult.DenialReason);
        var claimCarc = Assert.Single(ctx.AdjudicationResult.AdjustmentReasons);
        Assert.Equal("CO", claimCarc.GroupCode);
        Assert.Equal("18", claimCarc.ReasonCode);
        Assert.Equal(150m, claimCarc.Amount);

        Assert.Equal(2, ctx.LineAdjudicationResults.Count);
        Assert.All(ctx.LineAdjudicationResults, l =>
        {
            var carc = Assert.Single(l.AdjustmentReasons);
            Assert.Equal("CO", carc.GroupCode);
            Assert.Equal("18", carc.ReasonCode);
            Assert.Equal(0m, l.PaidAmount);
        });
        Assert.Equal(100m, ctx.LineAdjudicationResults[0].AdjustmentReasons[0].Amount);
        Assert.Equal(50m, ctx.LineAdjudicationResults[1].AdjustmentReasons[0].Amount);
    }

    [Fact]
    public async Task ExactDuplicate_queries_tenant_member_and_service_window_excluding_own_chain()
    {
        PriorClaimsAre();
        var ctx = NewContext(IncomingClaim());

        await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        await _repository.Received(1).FindDuplicateCandidatesAsync(
            TenantId, MemberId, Dos, Dos, "chain-incoming", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactDuplicate_matches_modifiers_regardless_of_order_and_case()
    {
        var prior = PriorClaim();
        prior.ClaimLines[0].Modifiers = new List<string> { "lt", "25" };
        var incoming = IncomingClaim();
        incoming.ClaimLines[0].Modifiers = new List<string> { "25", "LT" };
        PriorClaimsAre(prior);
        var ctx = NewContext(incoming);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("8")]
    public async Task Replacement_and_void_claims_are_never_flagged(string frequencyCode)
    {
        PriorClaimsAre(PriorClaim());
        var incoming = IncomingClaim();
        incoming.ClaimFrequencyCode = frequencyCode;
        var ctx = NewContext(incoming);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
        await _repository.DidNotReceiveWithAnyArgs().FindDuplicateCandidatesAsync(
            default!, default!, default, default, default!, default);
    }

    [Fact]
    public async Task Adjustment_version_referencing_its_original_is_never_flagged()
    {
        PriorClaimsAre(PriorClaim());
        var incoming = IncomingClaim();
        incoming.PredecessorVersionId = "prior-1";
        var ctx = NewContext(incoming);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task SuspectDuplicate_with_different_billing_provider_is_pended()
    {
        var prior = PriorClaim();
        prior.BillingProviderNPI = OtherNpi;
        PriorClaimsAre(prior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal(DuplicateClaimStage.DuplicatePendCode, ctx.PendDetails!.PendCode);
        Assert.Contains("Suspect duplicate", ctx.PendDetails.PendReason);
        Assert.Contains("billing provider", ctx.PendDetails.PendReason);
        Assert.Empty(ctx.PendDetails.EditFailures); // duplicates are not NCCI edits
        Assert.Equal(2, ctx.PendDetails.DuplicateFindings.Count);
        Assert.All(ctx.PendDetails.DuplicateFindings, f =>
        {
            Assert.Equal(DuplicateClaimStage.SuspectRuleId, f.RuleId);
            Assert.Equal(DuplicateClaimStage.SuspectDuplicateType, f.DuplicateType);
            Assert.Equal("prior-1", f.MatchedClaimId);
            Assert.Equal("CN-PRIOR", f.MatchedClaimNumber);
        });
        Assert.Equal(new[] { 1, 2 }, ctx.PendDetails.DuplicateFindings.Select(f => f.LineNumber));
        Assert.Equal(2, ctx.DuplicateFindings.Count);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task SuspectDuplicate_with_different_modifier_and_charge_is_pended()
    {
        var prior = PriorClaim();
        prior.ClaimLines.RemoveAt(1);
        prior.ClaimLines[0].Modifiers = new List<string> { "59" };
        prior.ClaimLines[0].ChargeAmount = 120m;
        var incoming = IncomingClaim();
        incoming.ClaimLines.RemoveAt(1);
        incoming.TotalChargeAmount = 100m;
        PriorClaimsAre(prior);
        var ctx = NewContext(incoming);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Contains("modifiers", ctx.PendDetails!.PendReason);
        Assert.Contains("charge", ctx.PendDetails.PendReason);
    }

    [Fact]
    public async Task Mixed_exact_and_clean_lines_pend_instead_of_denying()
    {
        var prior = PriorClaim();
        prior.ClaimLines.RemoveAt(1);          // only line 1 has a prior twin
        PriorClaimsAre(prior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        var finding = Assert.Single(ctx.PendDetails!.DuplicateFindings);
        Assert.Equal(DuplicateClaimStage.ExactRuleId, finding.RuleId);
        Assert.Equal(DuplicateClaimStage.ExactDuplicateType, finding.DuplicateType);
        Assert.Equal("18", finding.SuggestedCarc);
        Assert.Equal(1, finding.LineNumber);
        Assert.Equal(1, finding.MatchedLineNumber);
    }

    [Fact]
    public async Task Different_date_of_service_is_not_flagged()
    {
        var prior = PriorClaim();
        foreach (var line in prior.ClaimLines)
        {
            line.ServiceDateFrom = Dos.AddDays(1);
            line.ServiceDateTo = Dos.AddDays(1);
        }
        prior.ServiceDateFrom = prior.ServiceDateTo = Dos.AddDays(1);
        PriorClaimsAre(prior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task Claim_does_not_match_itself_or_its_own_version_chain()
    {
        var incoming = IncomingClaim();
        // The same row coming back from the store, plus an earlier version
        // of the same chain and a legacy genesis row keyed by the chain id.
        var self = AsStoredClaim(incoming);
        var priorVersion = PriorClaim("version-1", "chain-incoming");
        var legacyGenesis = PriorClaim("chain-incoming", claimVersionId: string.Empty);
        PriorClaimsAre(self, priorVersion, legacyGenesis);
        var ctx = NewContext(incoming);

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
    }

    [Theory]
    [InlineData(ClaimStatus.Denied, ClaimVersionState.Denied)]
    [InlineData(ClaimStatus.Voided, ClaimVersionState.Voided)]
    [InlineData(ClaimStatus.Paid, ClaimVersionState.Adjusted)]
    [InlineData(ClaimStatus.Submitted, ClaimVersionState.Draft)]
    public async Task Dead_prior_claims_never_count_as_originals(ClaimStatus status, ClaimVersionState state)
    {
        var prior = PriorClaim();
        prior.Status = status;
        prior.VersionState = state;
        PriorClaimsAre(prior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Prior_void_request_never_counts_as_an_original()
    {
        var prior = PriorClaim();
        prior.ClaimFrequencyCode = "8";
        PriorClaimsAre(prior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Claim_submitted_after_the_incoming_claim_is_not_its_original()
    {
        var later = PriorClaim();
        later.SubmittedDate = IncomingSubmitted.AddDays(1);
        PriorClaimsAre(later);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Exact_PendForReview_mode_pends_instead_of_denying()
    {
        PriorClaimsAre(PriorClaim());
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage(exact: DuplicateClaimEnforcementMode.PendForReview)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(DuplicateClaimStage.DuplicatePendCode, ctx.PendDetails!.PendCode);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task SoftValidation_modes_pass_without_payment_effect()
    {
        var suspectPrior = PriorClaim("prior-2");
        suspectPrior.BillingProviderNPI = OtherNpi;
        PriorClaimsAre(PriorClaim(), suspectPrior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage(
                exact: DuplicateClaimEnforcementMode.SoftValidation,
                suspect: DuplicateClaimEnforcementMode.SoftValidation)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Null(ctx.AdjudicationResult.DenialReasonCode);
    }

    [Fact]
    public async Task Prior_line_already_denied_as_duplicate_is_not_an_original()
    {
        var prior = PriorClaim();
        foreach (var line in prior.ClaimLines)
        {
            line.AdjudicationResult = new LineAdjudicationResult
            {
                AdjustmentReasons = new List<ClaimAdjustmentReason>
                {
                    new() { GroupCode = "CO", ReasonCode = "18", Amount = line.ChargeAmount },
                },
            };
        }
        PriorClaimsAre(prior);
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task Candidate_lookup_failure_pends_rather_than_rejecting()
    {
        _repository
            .FindDuplicateCandidatesAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));
        var ctx = NewContext(IncomingClaim());

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(DuplicateClaimStage.DuplicatePendCode, ctx.PendDetails!.PendCode);
    }

    // ── builders ─────────────────────────────────────────────────────────

    private static ClaimAdjudicationContext NewContext(AdapterClaim claim) => new()
    {
        TenantId = TenantId,
        ClaimVersionId = claim.ClaimVersionId,
        Claim = claim,
    };

    private static AdapterClaim IncomingClaim() => new()
    {
        TenantId = TenantId,
        Id = "incoming-1",
        ClaimVersionId = "chain-incoming",
        VersionNumber = 1,
        VersionState = ClaimVersionState.Submitted,
        ClaimNumber = "CN-INCOMING",
        MemberId = MemberId,
        BillingProviderNPI = BillingNpi,
        ClaimType = ClaimType.Professional,
        ClaimFrequencyCode = "1",
        Status = ClaimStatus.Submitted,
        TotalChargeAmount = 150m,
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
        SubmittedDate = IncomingSubmitted,
        ClaimLines = new List<AdapterClaimLine>
        {
            new()
            {
                LineNumber = 1, ProcedureCode = "99213", Modifiers = new List<string> { "25" },
                Units = 1, ChargeAmount = 100m, ServiceDateFrom = Dos, ServiceDateTo = Dos,
            },
            new()
            {
                LineNumber = 2, ProcedureCode = "36415",
                Units = 1, ChargeAmount = 50m, ServiceDateFrom = Dos, ServiceDateTo = Dos,
            },
        },
    };

    private static Claim PriorClaim(string id = "prior-1", string? claimVersionId = null) => new()
    {
        TenantId = TenantId,
        Id = id,
        ClaimVersionId = claimVersionId ?? id,
        VersionNumber = 1,
        VersionState = ClaimVersionState.Paid,
        ClaimNumber = "CN-PRIOR",
        MemberId = MemberId,
        BillingProviderNPI = BillingNpi,
        ClaimType = ClaimType.Professional,
        ClaimFrequencyCode = "1",
        Status = ClaimStatus.Paid,
        TotalChargeAmount = 150m,
        ServiceDateFrom = Dos,
        ServiceDateTo = Dos,
        SubmittedDate = PriorSubmitted,
        ClaimLines = new List<ClaimLine>
        {
            new()
            {
                LineNumber = 1, ProcedureCode = "99213", Modifiers = new List<string> { "25" },
                Units = 1, ChargeAmount = 100m, ServiceDateFrom = Dos, ServiceDateTo = Dos,
            },
            new()
            {
                LineNumber = 2, ProcedureCode = "36415",
                Units = 1, ChargeAmount = 50m, ServiceDateFrom = Dos, ServiceDateTo = Dos,
            },
        },
    };

    private static Claim AsStoredClaim(AdapterClaim claim)
    {
        var stored = claim.ToClaim();
        // Even with an earlier timestamp the row is the claim itself.
        stored.SubmittedDate = PriorSubmitted;
        return stored;
    }
}
