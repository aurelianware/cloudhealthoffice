using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Workbench step 2c on claim detail: a pended claim with edits shows them
/// with their policy; Pay becomes Finish; Finish shows the summary and sends
/// the overrides (everything else upheld) in one approval.
/// </summary>
public class ClaimFinishReviewTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IWorkQueueService> _workQueue = new();
    private IReadOnlyList<EditDecisionInput>? _sentDecisions;
    private IReadOnlyList<LineDenialInput>? _sentDenials;

    public ClaimFinishReviewTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_workQueue.Object);
        Services.AddSingleton(Mock.Of<IDialogService>());
        Services.AddSingleton(GatingUsers.With("claims:read", "claims:work", "workqueue:work").Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _claims.Setup(c => c.GetClaimByIdAsync("C1")).ReturnsAsync(new ClaimDetails
        {
            ClaimId = "C1",
            ClaimNumber = "MCC-P-0000695",
            Status = "Pended",
            PendDetails = new ClaimPendDetails { PendCode = "MUE", PendReason = "MUE limit", Fingerprint = "fp-1" },
        });
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimDuplicateMatch>());
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());
        _workQueue.Setup(w => w.ResolvePendedClaimAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<EditDecisionInput>?>(), It.IsAny<IReadOnlyList<LineDenialInput>?>()))
            .Callback((string _, string _, string _, string? _, string _, int? _, string? _,
                IReadOnlyList<EditDecisionInput>? d, IReadOnlyList<LineDenialInput>? l) => { _sentDecisions = d; _sentDenials = l; })
            .ReturnsAsync(ClaimResolutionResult.Resolved);
    }

    private void EditsAre(params ClaimEditItem[] edits) =>
        _claims.Setup(c => c.GetClaimEditsAsync("C1")).ReturnsAsync(new ClaimEditsView
        {
            ClaimId = "C1",
            PendFingerprint = "fp-1",
            Edits = edits.ToList(),
            DenialReasons = [new() { GroupCode = "CO", Carc = "50", Description = "Not medically necessary" }],
        });

    private static ClaimEditItem Mue(bool canOverride) => new()
    {
        EditKey = "MUE|NE002|66984|2026-09-16|1",
        EditType = "Mue",
        RuleId = "NE002",
        Message = "billed 2 units; MUE limit 1 (MAI 3)",
        LineNumbers = [1],
        OverrideLevel = "Supervisor",
        RequiresReason = true,
        RequiresDocumentation = true,
        CanOverride = canOverride,
        PolicyDescription = "MUE, MAI 3: per day, clinical",
        SuggestedCarc = "151",
    };

    private static ClaimEditItem Pair() => new()
    {
        EditKey = "NCCI|NE001|66984|66982|2",
        EditType = "NcciPair",
        RuleId = "NE001",
        Message = "66982 bundled into 66984",
        LineNumbers = [2],
        OverrideLevel = "Examiner",
        RequiresReason = true,
        CanOverride = true,
        PolicyDescription = "NCCI pair, modifier indicator 1",
        SuggestedCarc = "B20",
    };

    [Theory]
    [InlineData("summary")]
    [InlineData("edits")]
    [InlineData("history")]
    public void Header_Finish_shows_confirmation_above_the_record_tabs_without_submitting(string tab)
    {
        EditsAre(Mue(canOverride: false), Pair());
        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1").Add(x => x.Tab, tab));
        cut.WaitForElement("[data-testid=finish]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=finish-summary]").TextContent.Should().Contain("Confirm and finish"));
        var summary = cut.Find("[data-testid=finish-summary]");
        summary.Closest("[data-testid=edits-panel]").Should().BeNull();
        cut.Markup.IndexOf("data-testid=\"finish-summary\"", StringComparison.Ordinal)
            .Should().BeLessThan(cut.Markup.IndexOf("Manual Review Required", StringComparison.Ordinal));
        _workQueue.Verify(w => w.ResolvePendedClaimAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<EditDecisionInput>?>(), It.IsAny<IReadOnlyList<LineDenialInput>?>()), Times.Never);
    }

    [Fact]
    public void Edits_show_their_policy_and_Pay_becomes_Finish()
    {
        EditsAre(Mue(canOverride: false), Pair());

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1"));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=edit-row]").Should().HaveCount(2));
        cut.Find("[data-testid=finish]").TextContent.Should().Contain("Finish");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Pay");
        var policies = cut.FindAll("[data-testid=edit-policy]").Select(e => e.TextContent.Trim()).ToList();
        policies.Should().Equal("Supervisor, documentation", "Examiner, reason");
        cut.Markup.Should().Contain("Needs a supervisor to override.");
    }

    [Fact]
    public void Finish_shows_the_summary_and_sends_only_the_overrides()
    {
        EditsAre(Mue(canOverride: false), Pair());
        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-override]"));

        var toggle = cut.Find("[data-testid=edit-override]");
        (toggle.TagName == "INPUT" ? toggle : toggle.QuerySelector("input")!).Change(true);
        cut.Find("[data-testid=finish]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=finish-problem]").TextContent.Should().Contain("reason"));
        cut.Find("[data-testid=finish-confirm]").HasAttribute("disabled").Should().BeTrue();

        var reasonBox = cut.Find("[data-testid=edit-override-reason]");
        (reasonBox.TagName is "INPUT" or "TEXTAREA" ? reasonBox : reasonBox.QuerySelector("input,textarea")!).Input("Separate incision; modifier omitted");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=finish-problem]").Should().BeEmpty());
        cut.Find("[data-testid=finish-summary]").TextContent.Should().Contain("2 edits: 1 overridden, 1 upheld.");
        cut.Find("[data-testid=finish-confirm]").Click();

        cut.WaitForAssertion(() => _sentDecisions.Should().NotBeNull());
        _sentDecisions!.Should().Equal(new EditDecisionInput(Pair().EditKey, "Override", "Separate incision; modifier omitted"));
        _sentDenials.Should().BeEmpty();
        _workQueue.Verify(w => w.ResolvePendedClaimAsync("C1", "Approved", It.Is<string>(r => r.StartsWith("Finished review")),
            It.IsAny<string?>(), It.IsAny<string>(), null, "fp-1",
            It.IsAny<IReadOnlyList<EditDecisionInput>?>(), It.IsAny<IReadOnlyList<LineDenialInput>?>()), Times.Once);
    }

    [Fact]
    public void Without_edits_to_decide_the_page_keeps_whole_claim_Pay()
    {
        EditsAre();

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "C1"));

        cut.WaitForAssertion(() => cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Pay"));
        cut.FindAll("[data-testid=edits-panel]").Should().BeEmpty();
    }
}
