using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.CobEngine.Domain;
using CloudHealthOffice.CobEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.8 — behavior coverage for <see cref="CoordinationOfBenefitsStage"/>:
/// CHO-primary detection, CHO-secondary detection, mode-driven outcomes,
/// degradation handling, Medicare-primary differentiation, tertiary
/// classification, missing-member-id reject, engine-exception fallback.
/// Uses the REAL <see cref="PayerOrderService"/> so the engine surface
/// behaviour is exercised end-to-end (it's pure-calculation, no I/O).
/// </summary>
public class CoordinationOfBenefitsStageTests
{
    private const string TenantId = "tenant-1";
    private const string ClaimVersionId = "ver-cob-1";
    private const string MemberId = "MEM-1";

    private readonly ICoverageClient _coverageClient = Substitute.For<ICoverageClient>();
    private readonly IPayerOrderService _realPayerOrder = new PayerOrderService();

    private CoordinationOfBenefitsStage NewStage(
        CobEnforcementMode mode = CobEnforcementMode.PendForSecondary,
        IPayerOrderService? payerOrder = null)
    {
        var options = Options.Create(new TenantEnforcementPolicyOptions { CobMode = mode });
        return new CoordinationOfBenefitsStage(
            _coverageClient,
            payerOrder ?? _realPayerOrder,
            options,
            NullLogger<CoordinationOfBenefitsStage>.Instance);
    }

    [Fact]
    public void Stage_metadata_matches_pipeline_contract()
    {
        var stage = NewStage();
        Assert.Equal("CoordinationOfBenefits", stage.Name);
        Assert.Equal(500, stage.Order);
        // Decision 2 — disabling COB would let CHO-secondary claims
        // process as CHO-primary (wrong on the wire). Tenants set
        // CobMode=SoftValidation instead.
        Assert.True(stage.IsRequired);
    }

    [Fact]
    public async Task EmptyList_returns_Pass_with_ChoPrimaryNoSecondary()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CobEntry>());

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.CobResult);
        Assert.Equal(CobScenario.ChoPrimaryNoSecondary, ctx.CobResult!.Scenario);
        Assert.False(ctx.CobResult.IsMedicarePrimary);
        Assert.Null(ctx.CobResult.PendReason);
        Assert.Null(ctx.CobResult.AppliedRule);
        // No pend condition detected — BuildPrimaryOutcome is unchanged by
        // the Defect B fix and must not populate PendDetails.
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task AllSecondary_entries_return_Pass_with_ChoPrimaryWithSecondary()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "S" },
                new CobEntry { PayerName = "BCBS", PayerId = "BCB", CoverageSequence = "T" },
            });

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal(CobScenario.ChoPrimaryWithSecondary, ctx.CobResult!.Scenario);
        Assert.False(ctx.CobResult.IsMedicarePrimary);
        Assert.Null(ctx.CobResult.PendReason);
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task SinglePrimary_commercial_entry_pends_with_ExplicitCoverageRecord()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P", IsMedicare = false },
            });

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue, "Pend continues so subsequent stages can decorate");
        Assert.Equal(CobScenario.ChoSecondaryDetected, ctx.CobResult!.Scenario);
        Assert.False(ctx.CobResult.IsMedicarePrimary);
        Assert.Equal("Aetna", ctx.CobResult.PrimaryPayerName);
        Assert.Equal("AET", ctx.CobResult.PrimaryPayerId);
        Assert.Equal(
            CoordinationOfBenefitsStage.SecondaryNotSupportedPendReason,
            ctx.CobResult.PendReason);
        // Engine falls through to default for commercial-primary because
        // Phase 1 InsuredInfo lacks birthday / employment signals; stage
        // labels rule as ExplicitCoverageRecord (the wire signal IS
        // explicit).
        Assert.Equal(PayerOrderRule.ExplicitCoverageRecord, ctx.CobResult.AppliedRule);
        // Defect B fix — PendDetails must be populated so the projection
        // (PersistenceStage) has something to persist onto the claim record.
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
        Assert.Contains("Aetna", ctx.PendDetails.PendReason);
        Assert.Empty(ctx.PendDetails.EditFailures);
    }

    [Fact]
    public async Task SinglePrimary_Medicare_entry_pends_with_MedicareSecondaryPayer_rule()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Medicare", PayerId = "MED", CoverageSequence = "P", IsMedicare = true },
            });

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CobScenario.ChoSecondaryDetected, ctx.CobResult!.Scenario);
        Assert.True(ctx.CobResult.IsMedicarePrimary);
        // Medicare path through PayerOrderService produces
        // MedicareSecondaryPayer when MedicareDesignatedPrimary=true on
        // the other coverage (Decision 16a mapping).
        Assert.Equal(PayerOrderRule.MedicareSecondaryPayer, ctx.CobResult.AppliedRule);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task TwoPrimary_entries_classify_as_ChoTertiaryDetected()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
                new CobEntry { PayerName = "BCBS", PayerId = "BCB", CoverageSequence = "P" },
            });

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CobScenario.ChoTertiaryDetected, ctx.CobResult!.Scenario);
        Assert.Equal("Aetna", ctx.CobResult.PrimaryPayerName); // first primary wins
    }

    [Fact]
    public async Task PrimaryPlusSecondary_entries_classify_as_ChoTertiaryDetected()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
                new CobEntry { PayerName = "BCBS", PayerId = "BCB", CoverageSequence = "S" },
            });

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CobScenario.ChoTertiaryDetected, ctx.CobResult!.Scenario);
    }

    [Fact]
    public async Task DenyMode_on_secondary_detection_short_circuits_with_Deny()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
            });

        var ctx = NewContext();
        var result = await NewStage(CobEnforcementMode.Deny).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue, "Deny short-circuits to PersistenceStage");
        // Outcome still recorded for audit-trail richness.
        Assert.Equal(CobScenario.ChoSecondaryDetected, ctx.CobResult!.Scenario);
        // PendDetails is populated even in Deny mode — mirrors NcciEditsStage's
        // precedent (ApplyFailureSnapshots runs regardless of NcciMode). The
        // claim still ends up Denied (Deny outweighs Pend in the
        // orchestrator's Reject>Deny>Pend>Pass precedence — see
        // ClaimAdjudicationStageResult.ResolveOutcome), but the audit trail
        // explains why COB fired.
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task SoftValidation_on_secondary_detection_returns_Pass_but_records_outcome()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
            });

        var ctx = NewContext();
        var result = await NewStage(CobEnforcementMode.SoftValidation)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.Equal(CobScenario.ChoSecondaryDetected, ctx.CobResult!.Scenario);
        Assert.Equal(
            CoordinationOfBenefitsStage.SecondaryNotSupportedPendReason,
            ctx.CobResult.PendReason);
        // SoftValidation still records the audit-trail snapshot — telemetry
        // captures the detection even though the stage outcome is Pass.
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task NullFromClient_pends_in_PendForSecondary_mode()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null);

        var ctx = NewContext();
        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(CobScenario.None, ctx.CobResult!.Scenario);
        Assert.Equal(
            CoordinationOfBenefitsStage.CoverageServiceUnavailablePendReason,
            ctx.CobResult.PendReason);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
        Assert.Contains("Coverage-service unavailable", ctx.PendDetails.PendReason);
    }

    [Fact]
    public async Task NullFromClient_pends_in_Deny_mode_per_Decision_7()
    {
        // Decision 7 — coverage-service unavailable pends regardless of
        // mode; "unable to determine coverage state" is not a denial.
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null);

        var ctx = NewContext();
        var result = await NewStage(CobEnforcementMode.Deny).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.NotEqual(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.Equal(
            CoordinationOfBenefitsStage.CoverageServiceUnavailablePendReason,
            ctx.CobResult!.PendReason);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task NullFromClient_passes_in_SoftValidation_mode()
    {
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<CobEntry>?)null);

        var ctx = NewContext();
        var result = await NewStage(CobEnforcementMode.SoftValidation)
            .ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        // Outcome still recorded — telemetry captures the degradation
        // even when soft-validation suppresses the pend.
        Assert.Equal(CobScenario.None, ctx.CobResult!.Scenario);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task MissingMemberId_returns_Reject()
    {
        var ctx = NewContext(claim: new AdapterClaim
        {
            Id = "claim-1",
            ClaimNumber = "CLM-1",
            MemberId = "",
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        var result = await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Reject, result.Outcome);
        Assert.False(result.Continue);
        // Coverage client never invoked — short-circuited at member-id
        // gate. Stage records no CobResult; the upstream data quality
        // failure is the outcome.
        await _coverageClient.DidNotReceive().GetCobEntriesAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EngineException_during_rule_lookup_defaults_to_ExplicitCoverageRecord()
    {
        var brokenEngine = Substitute.For<IPayerOrderService>();
        brokenEngine
            .DetermineOrder(Arg.Any<InsuredInfo>(), Arg.Any<IReadOnlyList<InsuredInfo>>())
            .Throws(new InvalidOperationException("engine failure"));

        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new CobEntry { PayerName = "Aetna", PayerId = "AET", CoverageSequence = "P" },
            });

        var ctx = NewContext();
        var result = await NewStage(payerOrder: brokenEngine).ExecuteAsync(ctx, CancellationToken.None);

        // Pend posture preserved; rule defaults to ExplicitCoverageRecord
        // so audit trail still records why CHO is secondary.
        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(PayerOrderRule.ExplicitCoverageRecord, ctx.CobResult!.AppliedRule);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("COB", ctx.PendDetails!.PendCode);
    }

    [Fact]
    public async Task Stage_passes_earliest_service_date_to_coverage_client()
    {
        var earliest = new DateTime(2025, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var lineEarlier = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        var claim = new AdapterClaim
        {
            Id = "claim-1",
            ClaimNumber = "CLM-1",
            MemberId = MemberId,
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = earliest,
            ServiceDateTo = earliest,
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213",
                        ServiceDateFrom = lineEarlier, ServiceDateTo = lineEarlier },
            },
        };
        var ctx = NewContext(claim: claim);
        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CobEntry>());

        await NewStage().ExecuteAsync(ctx, CancellationToken.None);

        await _coverageClient.Received().GetCobEntriesAsync(
            TenantId,
            MemberId,
            Arg.Is<DateTime>(d => d == lineEarlier),
            false,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ResolveEarliestServiceDate_falls_back_to_now_when_claim_has_no_dates()
    {
        var claim = new AdapterClaim
        {
            Id = "claim-1",
            MemberId = MemberId,
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = default,
            ServiceDateTo = default,
        };
        var resolved = CoordinationOfBenefitsStage.ResolveEarliestServiceDate(claim);
        Assert.True(resolved > DateTime.UtcNow.AddSeconds(-5));
    }

    [Fact]
    public void ResolveEarliestServiceDate_uses_line_dates_when_header_is_default()
    {
        // Copilot review #737/4 — naive seed-from-header would never
        // pick up the line date because `line.ServiceDateFrom < default`
        // is structurally false. The fix treats default as null and
        // falls back to UtcNow only when ALL dates are missing.
        var lineDate = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            Id = "claim-1",
            MemberId = MemberId,
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = default,
            ServiceDateTo = default,
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213",
                        ServiceDateFrom = lineDate, ServiceDateTo = lineDate },
            },
        };

        var resolved = CoordinationOfBenefitsStage.ResolveEarliestServiceDate(claim);
        Assert.Equal(lineDate, resolved);
    }

    [Fact]
    public void ResolveEarliestServiceDate_picks_min_of_header_and_lines()
    {
        var headerDate = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var earlierLine = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var laterLine = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            Id = "claim-1",
            MemberId = MemberId,
            BillingProviderNPI = "1234567890",
            ServiceDateFrom = headerDate,
            ServiceDateTo = headerDate,
            ClaimLines = new List<AdapterClaimLine>
            {
                new() { LineNumber = 1, ProcedureCode = "99213",
                        ServiceDateFrom = laterLine, ServiceDateTo = laterLine },
                new() { LineNumber = 2, ProcedureCode = "99214",
                        ServiceDateFrom = earlierLine, ServiceDateTo = earlierLine },
            },
        };

        var resolved = CoordinationOfBenefitsStage.ResolveEarliestServiceDate(claim);
        Assert.Equal(earlierLine, resolved);
    }

    [Theory]
    [InlineData(CobScenario.ChoPrimaryNoSecondary)]
    [InlineData(CobScenario.ChoPrimaryWithSecondary)]
    public async Task PrimaryScenarios_do_not_invoke_engine_for_rule_lookup(CobScenario expected)
    {
        var spyEngine = Substitute.For<IPayerOrderService>();
        spyEngine.DetermineOrder(Arg.Any<InsuredInfo>(), Arg.Any<IReadOnlyList<InsuredInfo>>())
            .Returns(new PayerOrderResult
            {
                PayerSequence = PayerSequenceCode.Primary,
                Rule = PayerOrderRule.ExplicitCoverageRecord,
                Explanation = "should not be called",
            });

        _coverageClient.GetCobEntriesAsync(
                TenantId, MemberId, Arg.Any<DateTime>(), false, Arg.Any<CancellationToken>())
            .Returns(expected == CobScenario.ChoPrimaryNoSecondary
                ? Array.Empty<CobEntry>()
                : new[] { new CobEntry { PayerName = "Aetna", CoverageSequence = "S" } });

        var ctx = NewContext();
        await NewStage(payerOrder: spyEngine).ExecuteAsync(ctx, CancellationToken.None);

        spyEngine.DidNotReceive()
            .DetermineOrder(Arg.Any<InsuredInfo>(), Arg.Any<IReadOnlyList<InsuredInfo>>());
        Assert.Equal(expected, ctx.CobResult!.Scenario);
        Assert.Null(ctx.CobResult.AppliedRule);
    }

    private static AdapterClaim DefaultClaim() => new()
    {
        Id = "claim-1",
        ClaimNumber = "CLM-1",
        MemberId = MemberId,
        BillingProviderNPI = "1234567890",
        ServiceDateFrom = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2025, 5, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private static ClaimAdjudicationContext NewContext(AdapterClaim? claim = null) =>
        new()
        {
            TenantId = TenantId,
            ClaimVersionId = ClaimVersionId,
            Claim = claim ?? DefaultClaim(),
            ResolvedMember = new ResolvedMember
            {
                MemberId = MemberId,
                DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
        };
}
