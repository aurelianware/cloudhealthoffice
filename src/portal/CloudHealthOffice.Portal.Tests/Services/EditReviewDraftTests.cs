using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EditReviewDraftTests
{
    private static ClaimEditItem Edit(string key, string type = "NcciPair", bool canOverride = true,
        string level = "Examiner", bool requiresReason = true, params int[] lines) => new()
    {
        EditKey = key,
        EditType = type,
        RuleId = type == "Mue" ? "NE002" : "NE001",
        OverrideLevel = level,
        RequiresReason = requiresReason,
        CanOverride = canOverride,
        LineNumbers = lines.Length == 0 ? [2] : lines.ToList(),
        SuggestedCarc = type == "Mue" ? "151" : "B20",
    };

    private static EditReviewDraft Draft(params ClaimEditItem[] edits) => new(new ClaimEditsView
    {
        ClaimId = "C1",
        Edits = edits.ToList(),
        DenialReasons = [new() { GroupCode = "CO", Carc = "50", Description = "Not medically necessary" }],
    });

    [Fact]
    public void Every_edit_starts_upheld_and_nothing_is_sent_for_it()
    {
        var draft = Draft(Edit("a"), Edit("b", "Mue", lines: 1));

        draft.UpheldCount.Should().Be(2);
        draft.Decisions().Should().BeEmpty("the service upholds every edit not overridden");
        draft.Summary.Should().Be("2 edits: 0 overridden, 2 upheld.");
        draft.Effects().Should().Equal(
            "NE001 upheld — line 2 denied (CO-B20).",
            "NE002 on line 1 upheld — units over the limit denied (CO-151).");
    }

    [Fact]
    public void An_override_needs_a_reason_before_Finish()
    {
        var edit = Edit("a");
        var draft = Draft(edit);

        draft.SetOverride(edit, true);
        draft.Problems.Should().ContainSingle().Which.Should().Contain("reason");

        draft.SetOverrideReason(edit, "  Separate incision  ");
        draft.Problems.Should().BeEmpty();
        draft.Decisions().Should().Equal(new EditDecisionInput("a", "Override", "Separate incision"));
        draft.Summary.Should().Be("1 edit: 1 overridden, 0 upheld.");
    }

    [Fact]
    public void An_edit_the_reviewer_may_not_override_stays_upheld()
    {
        var edit = Edit("a", canOverride: false, level: "Never");
        var draft = Draft(edit);

        draft.SetOverride(edit, true);

        draft.IsOverridden(edit).Should().BeFalse();
        draft.Decisions().Should().BeEmpty();
    }

    [Fact]
    public void Switching_back_to_uphold_drops_the_override()
    {
        var edit = Edit("a");
        var draft = Draft(edit);
        draft.SetOverride(edit, true);
        draft.SetOverrideReason(edit, "x");

        draft.SetOverride(edit, false);

        draft.Decisions().Should().BeEmpty();
    }

    [Fact]
    public void Line_denials_need_a_listed_reason_code_and_a_reason_and_one_per_line()
    {
        var draft = Draft(Edit("a"));

        draft.AddLineDenial(1, "CO-45", "x").Should().NotBeNull();
        draft.AddLineDenial(1, "CO-50", " ").Should().NotBeNull();
        draft.AddLineDenial(1, "CO-50", "Records don't support it").Should().BeNull();
        draft.AddLineDenial(1, "CO-50", "again").Should().Contain("already denied");

        draft.LineDenials.Should().ContainSingle().Which.Should().Be(new LineDenialInput(1, null, "CO", "50", "Records don't support it"));
        draft.Summary.Should().Be("1 edit: 0 overridden, 1 upheld; 1 line denied by you.");

        draft.RemoveLineDenial(1);
        draft.LineDenials.Should().BeEmpty();
    }
}
