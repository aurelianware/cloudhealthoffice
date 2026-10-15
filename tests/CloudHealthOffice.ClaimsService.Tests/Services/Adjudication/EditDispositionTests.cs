using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using EngineModels = CloudHealthOffice.NcciEngine.Models;
using EngineServices = CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication;

/// <summary>
/// Edit-level dispositions on the examiner approval re-run: an edit is
/// identified by its facts, an overridden edit is suppressed, an upheld edit
/// denies what it covers, and an edit nobody decided still pends.
/// </summary>
public class EditDispositionTests
{
    private static readonly DateTime Dos = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    // ── Edit identity ──────────────────────────────────────────────────────

    [Fact]
    public void Identity_is_built_from_the_edits_facts_not_its_wording()
    {
        var a = Mue(lines: [2, 1], message: "billed 5 units");
        var b = Mue(lines: [1, 2], message: "reworded message");

        Assert.Equal(EditIdentity.Key(a), EditIdentity.Key(b));
        Assert.Equal("MUE|NE002|66984|2026-09-16|1,2", EditIdentity.Key(a));
    }

    [Fact]
    public void A_different_code_date_or_line_is_a_different_edit()
    {
        var baseKey = EditIdentity.Key(Mue());

        Assert.NotEqual(baseKey, EditIdentity.Key(Mue(code: "66982")));
        Assert.NotEqual(baseKey, EditIdentity.Key(Mue(serviceDate: Dos.AddDays(1))));
        Assert.NotEqual(baseKey, EditIdentity.Key(Mue(lines: [1])));
        Assert.Equal("NCCI|NE001|99213|99214|2", EditIdentity.Key(Pair()));
        Assert.Equal("DUP|DUP001|1|PRIOR-9|3", EditIdentity.Key(new DuplicateFindingSnapshot
        {
            RuleId = "DUP001", LineNumber = 1, MatchedClaimId = "prior-9", MatchedLineNumber = 3,
        }));
    }

    [Fact]
    public void The_engine_indicators_and_key_reach_the_pend_snapshot()
    {
        var snapshot = NcciEditsStage.MapFailure(new EngineModels.NcciEditFailure
        {
            EditType = EngineModels.NcciEditType.Mue,
            RuleId = "NE002",
            Column2Code = "66984",
            AffectedLineNumbers = [1],
            UnitsBilled = 2,
            MueMaxUnits = 1,
            MueAdjudicationIndicator = 3,
            ServiceDate = DateOnly.FromDateTime(Dos),
        });

        Assert.Equal(3, snapshot.MueAdjudicationIndicator);
        Assert.Equal(Dos, snapshot.ServiceDate);
        Assert.Equal("MUE|NE002|66984|2026-09-16|1", snapshot.EditKey);
    }

    // ── NCCI stage on the approval re-run ──────────────────────────────────

    [Fact]
    public async Task Decided_edits_are_settled_and_the_stage_passes()
    {
        var engine = Engine(EnginePair(), EngineMue(lines: [1]));
        var ctx = Context(Approval(
            Disposition(EditIdentity.Key(Pair()), EditDecision.Override, "Distinct procedure, separate incision"),
            Disposition(EditIdentity.Key(Mue(lines: [1])), EditDecision.Uphold)));

        var result = await Stage(engine).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(ctx.PendDetails);
        Assert.Contains(result.Notes, n => n.Contains("overridden by examiner") && n.Contains("separate incision"));
        Assert.Contains(result.Notes, n => n.Contains("upheld by examiner"));
        Assert.Equal(2, ctx.ExaminerOverrides.Count);
    }

    [Fact]
    public async Task An_edit_nobody_decided_still_pends_and_only_it_is_on_the_pend()
    {
        var engine = Engine(EnginePair(), EngineMue(lines: [1]));
        var ctx = Context(Approval(Disposition(EditIdentity.Key(Pair()), EditDecision.Override, "ok")));

        var result = await Stage(engine).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        var pended = Assert.Single(ctx.PendDetails!.EditFailures);
        Assert.Equal("NE002", pended.RuleId);
        Assert.Contains(result.Notes, n => n.Contains("overridden"));
    }

    [Fact]
    public async Task Without_dispositions_the_stage_behaves_as_before()
    {
        var engine = Engine(EnginePair());
        var ctx = Context(approval: null);

        var result = await Stage(engine).ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Single(ctx.PendDetails!.EditFailures);
        Assert.Empty(ctx.ExaminerOverrides);
    }

    // ── What an upheld edit denies ─────────────────────────────────────────

    [Fact]
    public void Upheld_pair_edit_denies_the_column_2_line_in_full()
    {
        var ctx = Context(Approval(reviewed: Pend(Pair()), Disposition(EditIdentity.Key(Pair()), EditDecision.Uphold)));

        var denial = Assert.Single(EditDispositionApplier.LineDenials(ctx));

        Assert.Equal(2, denial.LineNumber);
        Assert.True(denial.IsFullLine);
        Assert.Equal("B20", denial.Carc);
        Assert.Equal("N519", denial.Rarc);
    }

    [Fact]
    public void Upheld_MUE_denies_only_the_units_over_the_limit_from_the_last_line_back()
    {
        // Line 1: 3 units, line 2: 2 units; limit 3 per day → 2 units over, all on line 2.
        var mue = Mue(lines: [1, 2], unitsBilled: 5, max: 3);
        var ctx = Context(Approval(reviewed: Pend(mue), Disposition(EditIdentity.Key(mue), EditDecision.Uphold)),
            lineUnits: [3, 2]);

        var denial = Assert.Single(EditDispositionApplier.LineDenials(ctx));

        Assert.Equal(2, denial.LineNumber);
        Assert.Equal(2, denial.DeniedUnits);
        Assert.True(denial.IsFullLine);
        Assert.Equal("151", denial.Carc);
    }

    [Fact]
    public void Upheld_single_line_MUE_denies_part_of_the_line()
    {
        var mue = Mue(lines: [1], unitsBilled: 2, max: 1);
        var ctx = Context(Approval(reviewed: Pend(mue), Disposition(EditIdentity.Key(mue), EditDecision.Uphold)),
            lineUnits: [2, 1]);

        var denial = Assert.Single(EditDispositionApplier.LineDenials(ctx));

        Assert.Equal(1, denial.LineNumber);
        Assert.Equal(1, denial.DeniedUnits);
        Assert.False(denial.IsFullLine);
        Assert.Equal(0.5m, denial.PayableFraction);
    }

    [Fact]
    public void Upheld_duplicate_denies_the_line_with_CARC_18_and_overrides_deny_nothing()
    {
        var dup = new DuplicateFindingSnapshot { RuleId = "DUP001", LineNumber = 1, MatchedClaimId = "p", MatchedLineNumber = 1, SuggestedCarc = "18" };
        var pend = new PendDetails { PendCode = "DUPLICATE", DuplicateFindings = [dup], EditFailures = [Pair()] };
        var ctx = Context(Approval(reviewed: pend,
            Disposition(EditIdentity.Key(dup), EditDecision.Uphold),
            Disposition(EditIdentity.Key(Pair()), EditDecision.Override, "ok")));

        var denial = Assert.Single(EditDispositionApplier.LineDenials(ctx));

        Assert.Equal(1, denial.LineNumber);
        Assert.Equal("18", denial.Carc);
    }

    [Fact]
    public void A_partial_denial_reduces_allowed_and_the_line_still_balances()
    {
        // $300 billed for 3 units, priced at $150; 1 unit denied.
        var denial = new UpheldEditLineDenial(1, 1, 3, "151", "N115", "k");
        var allowed = new Dictionary<int, decimal> { [1] = 150m, [2] = 80m };

        EditDispositionApplier.ApplyToAllowed(allowed, [denial]);

        Assert.Equal(100m, allowed[1]);
        Assert.Equal(80m, allowed[2]);

        // The engine then writes CO-45 = billed − allowed = $200.
        var lines = new List<AdapterClaimLine>
        {
            new() { LineNumber = 1, Units = 3, ChargeAmount = 300m },
            new() { LineNumber = 2, Units = 1, ChargeAmount = 100m },
        };
        var results = new List<LineAdjudicationResult>
        {
            new() { AllowedAmount = 100m, AdjustmentReasons = [new() { GroupCode = "CO", ReasonCode = "45", Amount = 200m }] },
            new() { AllowedAmount = 80m, AdjustmentReasons = [new() { GroupCode = "CO", ReasonCode = "45", Amount = 20m }] },
        };

        EditDispositionApplier.ApplyToLineAdjustments(lines, results, [denial]);

        var line1 = results[0].AdjustmentReasons;
        Assert.Equal(("CO", "151", "N115", 100m), (line1[0].GroupCode, line1[0].ReasonCode, line1[0].RemarkCode, line1[0].Amount));
        Assert.Equal(100m, line1.Single(r => r.ReasonCode == "45").Amount);
        Assert.Equal(300m, results[0].AllowedAmount + line1.Sum(r => r.Amount));
        Assert.Equal(20m, results[1].AdjustmentReasons.Single().Amount);
    }

    [Fact]
    public void A_full_denial_replaces_CO_45_entirely()
    {
        var lines = new List<AdapterClaimLine> { new() { LineNumber = 2, Units = 1, ChargeAmount = 120m } };
        var results = new List<LineAdjudicationResult>
        {
            new() { AllowedAmount = 0m, AdjustmentReasons = [new() { GroupCode = "CO", ReasonCode = "45", Amount = 120m }] },
        };

        EditDispositionApplier.ApplyToLineAdjustments(lines, results, [new UpheldEditLineDenial(2, 1, 1, "B20", "N519", "k")]);

        var only = Assert.Single(results[0].AdjustmentReasons);
        Assert.Equal(("CO", "B20", 120m), (only.GroupCode, only.ReasonCode, only.Amount));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static NcciEditFailureSnapshot Mue(
        int[]? lines = null, string code = "66984", DateTime? serviceDate = null,
        decimal unitsBilled = 2, int max = 1, string message = "MUE") => new()
    {
        EditType = "Mue",
        RuleId = "NE002",
        Message = message,
        Column2Code = code,
        AffectedLineNumbers = (lines ?? [1, 2]).ToList(),
        ServiceDate = serviceDate ?? Dos,
        UnitsBilled = unitsBilled,
        MueMaxUnits = max,
        MueAdjudicationIndicator = 3,
        SuggestedCarc = "151",
        SuggestedRarc = "N115",
    };

    private static NcciEditFailureSnapshot Pair() => new()
    {
        EditType = "NcciPair",
        RuleId = "NE001",
        Column1Code = "99213",
        Column2Code = "99214",
        AffectedLineNumbers = [2],
        ModifierIndicator = 1,
        SuggestedCarc = "B20",
        SuggestedRarc = "N519",
    };

    private static EngineModels.NcciEditFailure EnginePair() => new()
    {
        EditType = EngineModels.NcciEditType.NcciPair,
        RuleId = "NE001",
        Message = "bundled",
        Column1Code = "99213",
        Column2Code = "99214",
        AffectedLineNumbers = [2],
        ModifierIndicator = 1,
        SuggestedCarc = "B20",
        SuggestedRarc = "N519",
    };

    private static EngineModels.NcciEditFailure EngineMue(int[] lines) => new()
    {
        EditType = EngineModels.NcciEditType.Mue,
        RuleId = "NE002",
        Message = "over the MUE",
        Column2Code = "66984",
        AffectedLineNumbers = lines.ToList(),
        UnitsBilled = 2,
        MueMaxUnits = 1,
        MueAdjudicationIndicator = 3,
        ServiceDate = DateOnly.FromDateTime(Dos),
        SuggestedCarc = "151",
        SuggestedRarc = "N115",
    };

    private static PendDetails Pend(params NcciEditFailureSnapshot[] edits) =>
        new() { PendCode = "NCCI", EditFailures = edits.ToList() };

    private static EditDisposition Disposition(string key, EditDecision decision, string? reason = null) => new()
    {
        EditKey = key,
        Decision = decision,
        Reason = reason,
        DecidedBy = "examiner-1",
        DecidedAt = Dos,
    };

    private static ExaminerApproval Approval(params EditDisposition[] dispositions) =>
        Approval(reviewed: null, dispositions);

    private static ExaminerApproval Approval(PendDetails? reviewed, params EditDisposition[] dispositions) => new()
    {
        ExaminerId = "examiner-1",
        ReviewedPend = reviewed,
        EditDispositions = dispositions,
    };

    private static EngineServices.INcciEditService Engine(params EngineModels.NcciEditFailure[] failures)
    {
        var engine = Substitute.For<EngineServices.INcciEditService>();
        engine.ScrubAsync(Arg.Any<EngineModels.NcciScrubRequest>(), Arg.Any<CancellationToken>())
            .Returns(new EngineModels.NcciScrubResult { ClaimId = "v1", EditFailures = failures.ToList() });
        return engine;
    }

    private static NcciEditsStage Stage(EngineServices.INcciEditService engine) =>
        new(engine, Options.Create(new TenantEnforcementPolicyOptions { NcciMode = NcciEnforcementMode.PendForReview }),
            NullLogger<NcciEditsStage>.Instance);

    private static ClaimAdjudicationContext Context(ExaminerApproval? approval, decimal[]? lineUnits = null)
    {
        var units = lineUnits ?? [1, 1];
        return new ClaimAdjudicationContext
        {
            TenantId = "tenant-1",
            ClaimVersionId = "v1",
            ExaminerApproval = approval,
            Claim = new AdapterClaim
            {
                TenantId = "tenant-1",
                Id = "v1",
                ClaimVersionId = "v1",
                ClaimNumber = "CLM-1",
                MemberId = "M1",
                BillingProviderNPI = "1234567890",
                ClaimType = ClaimType.Professional,
                PlaceOfServiceCode = "11",
                ServiceDateFrom = Dos,
                ServiceDateTo = Dos,
                ClaimLines = units.Select((u, i) => new AdapterClaimLine
                {
                    LineNumber = i + 1,
                    ProcedureCode = i == 0 ? "99213" : "99214",
                    Units = u,
                    ChargeAmount = 100m * u,
                    ServiceDateFrom = Dos,
                    ServiceDateTo = Dos,
                    DiagnosisPointers = [1],
                }).ToList(),
                DiagnosisCodes = [new() { Code = "H25.11", PointerNumber = 1 }],
            },
        };
    }
}
