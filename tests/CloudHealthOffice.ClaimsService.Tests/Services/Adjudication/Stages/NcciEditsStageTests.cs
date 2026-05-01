using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using EngineModels = CloudHealthOffice.NcciEngine.Models;
using EngineServices = CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// Capability 5.7 — behavior coverage for <see cref="NcciEditsStage"/>:
/// engine result → stage outcome translation per <see cref="NcciEnforcementMode"/>,
/// PendDetails accumulation on the context, exception → mode-driven outcome,
/// no-valid-lines soft-pass, mapping fidelity.
/// </summary>
public class NcciEditsStageTests
{
    private const string TenantId = "tenant-1";
    private const string ClaimVersionId = "ver-ncci-1";

    private readonly EngineServices.INcciEditService _engine = Substitute.For<EngineServices.INcciEditService>();

    private NcciEditsStage NewStage(NcciEnforcementMode mode = NcciEnforcementMode.PendForReview)
    {
        var options = Options.Create(new TenantEnforcementPolicyOptions { NcciMode = mode });
        return new NcciEditsStage(_engine, options, NullLogger<NcciEditsStage>.Instance);
    }

    [Fact]
    public void Stage_metadata_matches_pipeline_contract()
    {
        var stage = NewStage();
        Assert.Equal("NcciEdits", stage.Name);
        Assert.Equal(400, stage.Order);
        // Decision 3 — NCCI is foundational; orchestrator must not allow
        // per-tenant disablement (tenants set Mode=SoftValidation instead).
        Assert.True(stage.IsRequired);
    }

    [Fact]
    public async Task Engine_clean_result_returns_Pass_with_no_PendDetails()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(new EngineModels.NcciScrubResult
            {
                ClaimId = ClaimVersionId,
                NcciPairsChecked = 1,
                MueChecked = 2,
            });

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task NcciPair_failure_in_PendForReview_returns_Pend_with_snapshot()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResultWithFailure(NewNcciPairFailure()));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.PendForReview);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.Continue); // Pend continues so subsequent stages can decorate
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("NCCI", ctx.PendDetails!.PendCode);
        Assert.Single(ctx.PendDetails.EditFailures);

        var snapshot = ctx.PendDetails.EditFailures[0];
        Assert.Equal("NcciPair", snapshot.EditType);
        Assert.Equal("NE001", snapshot.RuleId);
        Assert.Equal("99213", snapshot.Column1Code);
        Assert.Equal("99214", snapshot.Column2Code);
        Assert.Single(snapshot.AffectedLineNumbers);
        Assert.Equal(2, snapshot.AffectedLineNumbers[0]);
        Assert.Equal("B20", snapshot.SuggestedCarc);
        Assert.True(snapshot.IsModifierAddressable());
    }

    [Fact]
    public async Task Mue_failure_in_PendForReview_uses_Mue_pendCode()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResultWithFailure(NewMueFailure()));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.PendForReview);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.NotNull(ctx.PendDetails);
        Assert.Equal("MUE", ctx.PendDetails!.PendCode);
        var snapshot = ctx.PendDetails.EditFailures[0];
        Assert.Equal("Mue", snapshot.EditType);
        Assert.Equal("NE002", snapshot.RuleId);
        Assert.Equal(5m, snapshot.UnitsBilled);
        Assert.Equal(3, snapshot.MueMaxUnits);
        Assert.False(snapshot.IsModifierAddressable());
    }

    [Fact]
    public async Task Mixed_failures_use_NCCI_umbrella_pendCode()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResultWithFailures(NewNcciPairFailure(), NewMueFailure()));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.PendForReview);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal("NCCI", ctx.PendDetails!.PendCode);
        Assert.Equal(2, ctx.PendDetails.EditFailures.Count);
        Assert.Contains("2 NCCI/MUE edit failures", ctx.PendDetails.PendReason);
    }

    [Fact]
    public async Task Failure_in_Deny_mode_returns_Deny_short_circuits_pipeline()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResultWithFailure(NewNcciPairFailure()));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.Deny);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        // Decision 5 — Deny factory (terminal benefit-side denial), not
        // Reject (which is reserved for structural pre-adjudication).
        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.NotNull(ctx.PendDetails); // failures still recorded for audit
    }

    [Fact]
    public async Task Failure_in_SoftValidation_mode_returns_Pass_but_keeps_failures_on_context()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(BuildResultWithFailure(NewNcciPairFailure()));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.SoftValidation);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        Assert.NotNull(ctx.PendDetails); // observability — failures persist for telemetry
        Assert.Single(ctx.PendDetails!.EditFailures);
    }

    [Fact]
    public async Task Engine_throws_in_PendForReview_returns_Pend_with_synthetic_snapshot()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("repository unavailable"));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.PendForReview);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.NotNull(ctx.PendDetails);
        var snapshot = ctx.PendDetails!.EditFailures[0];
        Assert.Equal("ENGINE_EXCEPTION", snapshot.RuleId);
        Assert.Equal("EngineError", snapshot.EditType);
        // Exception detail goes to logs only — message keeps the type
        // name for ops triage but doesn't leak ex.Message into PHI surface.
        Assert.Contains("InvalidOperationException", snapshot.Message);
    }

    [Fact]
    public async Task Engine_throws_in_Deny_mode_routes_to_Deny()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("repository unavailable"));

        var ctx = NewContext();
        var sut = NewStage(NcciEnforcementMode.Deny);

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
    }

    [Fact]
    public async Task Single_line_claim_runs_without_NcciPair_check_and_passes_when_no_MUE()
    {
        _engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(new EngineModels.NcciScrubResult
            {
                ClaimId = ClaimVersionId,
                NcciPairsChecked = 0,
                MueChecked = 1,
            });

        var ctx = NewContext();
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
    }

    [Fact]
    public async Task No_engine_valid_lines_short_circuits_to_softpass_without_calling_engine()
    {
        var ctx = NewContext(c =>
        {
            // Engine [Required] [MinLength(1)] would throw if we called
            // it with zero lines; mapper filters invalid lines first.
            c.Claim.ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1,
                    ProcedureCode = "ABC",   // not 5 chars — engine validation would reject
                    Units = 1m,
                    ServiceDateFrom = DateTime.UtcNow,
                },
            };
        });
        var sut = NewStage();

        var result = await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.True(result.Continue);
        await _engine.DidNotReceive().ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Mapper_passes_request_with_engine_valid_lines_only()
    {
        EngineModels.NcciScrubRequest? captured = null;
        _engine.ScrubAsync(Arg.Do<EngineModels.NcciScrubRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new EngineModels.NcciScrubResult { ClaimId = ClaimVersionId });

        var ctx = NewContext(c =>
        {
            c.Claim.ClaimLines = new List<AdapterClaimLine>
            {
                new()
                {
                    LineNumber = 1, ProcedureCode = "99213", Units = 1m,
                    ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                    Modifiers = new List<string> { "59" },
                },
                new()
                {
                    LineNumber = 2, ProcedureCode = "BAD", Units = 1m,        // bad code → filtered
                    ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                },
                new()
                {
                    LineNumber = 3, ProcedureCode = "99214", Units = 0m,      // zero units → filtered
                    ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                },
            };
        });

        var sut = NewStage();
        await sut.ExecuteAsync(ctx, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Single(captured!.ServiceLines);
        Assert.Equal("99213", captured.ServiceLines[0].ProcedureCode);
        Assert.Equal("837P", captured.ClaimType); // Professional
        Assert.Equal(new DateOnly(2026, 4, 15), captured.EffectiveDate);
        Assert.Equal(TenantId, captured.TenantId);
        Assert.Equal(ClaimVersionId, captured.ClaimId);
    }

    [Fact]
    public void MapEditType_covers_known_values_and_falls_back_to_Unknown()
    {
        Assert.Equal("NcciPair", NcciEditsStage.MapEditType(EngineModels.NcciEditType.NcciPair));
        Assert.Equal("Mue", NcciEditsStage.MapEditType(EngineModels.NcciEditType.Mue));
        Assert.Equal("Unknown", NcciEditsStage.MapEditType((EngineModels.NcciEditType)999));
    }

    [Fact]
    public void MapFailure_copies_all_engine_fields_through_to_snapshot()
    {
        var failure = new EngineModels.NcciEditFailure
        {
            EditType = EngineModels.NcciEditType.NcciPair,
            RuleId = "NE001",
            Message = "bundling violation",
            Column1Code = "29881",
            Column2Code = "29870",
            AffectedLineNumbers = new List<int> { 2 },
            ModifierOverridePresent = false,
            UnitsBilled = null,
            MueMaxUnits = null,
            SuggestedCarc = "B20",
            SuggestedRarc = "N519",
        };

        var snapshot = NcciEditsStage.MapFailure(failure);

        Assert.Equal("NcciPair", snapshot.EditType);
        Assert.Equal("NE001", snapshot.RuleId);
        Assert.Equal("bundling violation", snapshot.Message);
        Assert.Equal("29881", snapshot.Column1Code);
        Assert.Equal("29870", snapshot.Column2Code);
        Assert.Single(snapshot.AffectedLineNumbers);
        Assert.False(snapshot.ModifierOverridePresent);
        Assert.Equal("B20", snapshot.SuggestedCarc);
        Assert.Equal("N519", snapshot.SuggestedRarc);
    }

    private static EngineModels.NcciScrubResult BuildResultWithFailure(EngineModels.NcciEditFailure failure)
        => BuildResultWithFailures(failure);

    private static EngineModels.NcciScrubResult BuildResultWithFailures(params EngineModels.NcciEditFailure[] failures)
        => new()
        {
            ClaimId = ClaimVersionId,
            NcciPairsChecked = 1,
            MueChecked = 1,
            EditFailures = failures.ToList(),
        };

    private static EngineModels.NcciEditFailure NewNcciPairFailure() => new()
    {
        EditType = EngineModels.NcciEditType.NcciPair,
        RuleId = "NE001",
        Message = "Procedure 99214 is bundled into 99213. A -59/X modifier on line 2 is required to bill separately.",
        Column1Code = "99213",
        Column2Code = "99214",
        AffectedLineNumbers = new List<int> { 2 },
        ModifierOverridePresent = false,
        SuggestedCarc = "B20",
        SuggestedRarc = "N519",
    };

    private static EngineModels.NcciEditFailure NewMueFailure() => new()
    {
        EditType = EngineModels.NcciEditType.Mue,
        RuleId = "NE002",
        Message = "Procedure 99213 billed 5 units but the MUE limit is 3 unit(s) per day (MAI 2).",
        Column2Code = "99213",
        AffectedLineNumbers = new List<int> { 1, 2 },
        UnitsBilled = 5m,
        MueMaxUnits = 3,
        SuggestedCarc = "151",
        SuggestedRarc = "N115",
    };

    private static ClaimAdjudicationContext NewContext(Action<ClaimAdjudicationContext>? mutate = null)
    {
        var ctx = new ClaimAdjudicationContext
        {
            TenantId = TenantId,
            ClaimVersionId = ClaimVersionId,
            Claim = new AdapterClaim
            {
                TenantId = TenantId,
                Id = ClaimVersionId,
                ClaimVersionId = ClaimVersionId,
                ClaimNumber = "CLM-NCCI-1",
                MemberId = "MEM-1",
                BillingProviderNPI = "1234567890",
                ClaimType = ClaimType.Professional,
                PlaceOfServiceCode = "11",
                TotalChargeAmount = 200m,
                ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                ClaimLines = new List<AdapterClaimLine>
                {
                    new()
                    {
                        LineNumber = 1, ProcedureCode = "99213", Units = 1m, ChargeAmount = 100m,
                        ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                    },
                    new()
                    {
                        LineNumber = 2, ProcedureCode = "99214", Units = 1m, ChargeAmount = 100m,
                        ServiceDateFrom = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc),
                    },
                },
            },
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                DateOfBirth = new DateTime(1980, 6, 15, 0, 0, 0, DateTimeKind.Utc),
            },
        };
        mutate?.Invoke(ctx);
        return ctx;
    }
}
