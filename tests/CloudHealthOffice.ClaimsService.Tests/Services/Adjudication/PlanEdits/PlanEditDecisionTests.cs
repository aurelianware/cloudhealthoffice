using ClaimsService.Models;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.PlanEdits.Contracts;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.PlanEdits;

/// <summary>
/// A plan edit on a pend behaves like any CHO edit: it has a stable identity,
/// it is listed for the reviewer under the PLAN_EDIT override policy, it is
/// part of the pend fingerprint, it survives a later stage's pend, and an
/// upheld denial edit denies exactly the lines it names.
/// </summary>
public class PlanEditDecisionTests
{
    private static PlanEditFindingSnapshot Edit(string ruleId = "PLAN-NC-7", int[]? lines = null, string? carc = "96", string? rarc = null) =>
        PlanEditsStage.ToSnapshot(new PlanEdit
        {
            RuleId = ruleId,
            Message = "message",
            LineNumbers = lines ?? [1],
            SuggestedCarc = carc,
            SuggestedRarc = rarc,
        });

    // ── Identity ──────────────────────────────────────────────────────────

    [Fact]
    public void Identity_is_rule_and_lines_never_the_message_or_line_order()
    {
        var a = Edit(lines: [3, 1, 1]);
        var b = PlanEditsStage.ToSnapshot(new PlanEdit { RuleId = "plan-nc-7", Message = "reworded", LineNumbers = [1, 3], SuggestedCarc = "96" });

        Assert.Equal("PLAN|PLAN-NC-7|1,3", a.EditKey);
        Assert.Equal(a.EditKey, b.EditKey);
        Assert.Equal([1, 3], a.LineNumbers);
        Assert.NotEqual(a.EditKey, Edit(lines: [1]).EditKey);
        Assert.Equal("PLAN|CLAIM-LEVEL|", Edit("CLAIM-LEVEL", lines: [], carc: null).EditKey);
    }

    [Fact]
    public void A_key_naming_very_many_lines_stays_within_the_limit_and_stable()
    {
        var lines = Enumerable.Range(100, 300).ToArray();

        var key = Edit(lines: lines).EditKey!;

        Assert.True(key.Length <= EditIdentity.MaxKeyLength);
        Assert.StartsWith("PLAN|PLAN-NC-7|H", key);
        Assert.Equal(key, Edit(lines: lines.Reverse().ToArray()).EditKey);
        Assert.NotEqual(key, Edit(lines: lines.Skip(1).ToArray()).EditKey);
    }

    // ── Listed for the reviewer ───────────────────────────────────────────

    [Fact]
    public void Plan_edits_are_listed_as_decidable_edits_under_the_PLAN_EDIT_policy()
    {
        var pend = new PendDetails { PendCode = "PLANEDIT", PlanEditFindings = [Edit(), Edit("REVIEW-1", lines: [2], carc: null)] };

        var edits = ClaimEditCatalog.Edits(pend);

        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(ClaimEditCatalog.PlanEditType, e.EditType));
        Assert.All(edits, e => Assert.Equal(EditOverridePolicy.PlanEdit, e.Category));
        Assert.Equal("96", edits.Single(e => e.RuleId == "PLAN-NC-7").SuggestedCarc);
    }

    [Fact]
    public void The_default_policy_lets_an_examiner_override_a_plan_edit_with_a_reason_on_every_line_of_business()
    {
        foreach (var lob in new[] { LineOfBusiness.Commercial, LineOfBusiness.Medicare, LineOfBusiness.Medicaid })
        {
            var rule = EditOverridePolicy.Default.RuleFor("tenant-1", lob, EditOverridePolicy.PlanEdit);
            Assert.Equal(OverrideLevel.Examiner, rule.Level);
            Assert.True(rule.RequiresReason);
        }
    }

    // ── Fingerprint ───────────────────────────────────────────────────────

    [Fact]
    public void The_pend_fingerprint_changes_when_the_plan_edits_do_and_not_otherwise()
    {
        PendDetails Pend(params PlanEditFindingSnapshot[] edits) => new()
        {
            PendCode = "PLANEDIT",
            PendReason = "Plan edit PLAN-NC-7: message",
            PlanEditFindings = edits.ToList(),
        };

        var baseline = PendDetails.ComputeFingerprint(Pend(Edit(), Edit("R2", carc: null)));

        Assert.Equal(baseline, PendDetails.ComputeFingerprint(Pend(Edit("R2", carc: null), Edit())));
        Assert.NotEqual(baseline, PendDetails.ComputeFingerprint(Pend(Edit())));
        Assert.NotEqual(baseline, PendDetails.ComputeFingerprint(Pend(Edit(lines: [2]), Edit("R2", carc: null))));
        Assert.NotEqual(baseline, PendDetails.ComputeFingerprint(Pend(Edit(carc: "204"), Edit("R2", carc: null))));
    }

    [Fact]
    public void A_pend_without_plan_edits_keeps_its_existing_fingerprint()
    {
        // Pends recorded before plan edits existed must not change identity.
        var pend = new PendDetails { PendCode = "NCCI", PendReason = "r" };

        Assert.Equal("v2-", PendDetails.ComputeFingerprint(pend)[..3]);
        Assert.Equal(PendDetails.ComputeFingerprint(pend),
            PendDetails.ComputeFingerprint(new PendDetails { PendCode = "NCCI", PendReason = "r", PlanEditFindings = [] }));
    }

    // ── Surviving later stages ────────────────────────────────────────────

    [Fact]
    public void A_later_pend_merged_into_an_earlier_one_keeps_the_plan_edits()
    {
        var earlier = new PendDetails { PendCode = "NCCI", PendReason = "pair" };
        var later = new PendDetails { PendCode = "PLANEDIT", PendReason = "plan", PlanEditFindings = [Edit()] };

        ClaimAdjudicationOrchestrator.MergeInto(earlier, later);

        Assert.Single(earlier.PlanEditFindings);
        Assert.Contains("PLANEDIT: plan", earlier.AdditionalPendReasons);
    }

    // ── What an upheld edit denies ────────────────────────────────────────

    [Fact]
    public void An_upheld_denial_edit_denies_its_lines_in_full_group_CO_with_the_plans_codes()
    {
        var denial = Edit(lines: [1, 2], rarc: "N130");
        var ctx = PlanEditsStageTests.Context(new ExaminerApproval
        {
            ExaminerId = "examiner-1",
            ReviewedPend = new PendDetails { PendCode = "PLANEDIT", PlanEditFindings = [denial] },
            EditDispositions = [new EditDisposition { EditKey = denial.EditKey!, Decision = EditDecision.Uphold }],
        });

        var denials = EditDispositionApplier.LineDenials(ctx);

        Assert.Equal([1, 2], denials.Select(d => d.LineNumber));
        Assert.All(denials, d =>
        {
            Assert.True(d.IsFullLine);
            Assert.Equal("CO", d.Group);
            Assert.Equal("96", d.Carc);
            Assert.Equal("N130", d.Rarc);
        });
    }

    [Fact]
    public void An_upheld_review_edit_or_an_overridden_denial_edit_denies_nothing()
    {
        var review = Edit("REVIEW-1", lines: [1], carc: null);
        var denial = Edit(lines: [2]);
        var ctx = PlanEditsStageTests.Context(new ExaminerApproval
        {
            ExaminerId = "examiner-1",
            ReviewedPend = new PendDetails { PendCode = "PLANEDIT", PlanEditFindings = [review, denial] },
            EditDispositions =
            [
                new EditDisposition { EditKey = review.EditKey!, Decision = EditDecision.Uphold, UpheldByDefault = true },
                new EditDisposition { EditKey = denial.EditKey!, Decision = EditDecision.Override, Reason = "covered by rider" },
            ],
        });

        Assert.Empty(EditDispositionApplier.LineDenials(ctx));
    }

    // ── Transparency ──────────────────────────────────────────────────────

    [Fact]
    public void Transparency_reports_plan_edits_apart_from_NCCI_and_duplicates()
    {
        var claim = new Claim
        {
            TenantId = "tenant-1",
            Id = "c1",
            ClaimNumber = "CLM-1",
            PendDetails = new PendDetails { PendCode = "PLANEDIT", PlanEditFindings = [Edit(lines: [2]), Edit("REVIEW-1", carc: null)] },
        };

        var data = AdjudicationTransparencyBuilder.Build(claim)!;

        Assert.Empty(data.NcciResults);
        Assert.Equal(2, data.PlanEditResults.Count);
        Assert.Equal("Suggested CARC 96", data.PlanEditResults.Single(r => r.EditCode == "PLAN-NC-7").FailureReason);
        Assert.Equal("Review edit", data.PlanEditResults.Single(r => r.EditCode == "REVIEW-1").FailureReason);
    }
}
