using System.Text.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Rate change reprocessing page: measure a fee schedule change against paid
/// claims, propose a reprocessing run, and approve (someone else) and execute it.
/// </summary>
public class RetroRateReprocessingPageTests : TestContext
{
    private readonly Mock<IRetroRateService> _retro = new();
    private readonly Mock<IUserContextService> _user = new();

    private static readonly RetroRateImpactRunView Impact = new()
    {
        Id = "imp-1", FeeScheduleId = "t:TX FFS:20260101", FeeScheduleName = "TX FFS", NewRevision = 3,
        ChangeKind = "Replaced", EffectiveDate = new DateTime(2026, 1, 1), CandidatesRead = 40, ClaimsAffected = 3,
        CountsByDirection = new() { ["Underpaid"] = 2, ["Overpaid"] = 1, ["Unchanged"] = 0, ["Undecided"] = 0 },
        TotalUnderpaid = 120m, TotalOverpaid = 15m, NetPaidDelta = 105m,
        Notes = ["Nothing was changed."],
    };

    public RetroRateReprocessingPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_retro.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "adjuster-1", Roles = ["ClaimsSupervisor"] });
        _user.Setup(u => u.HasPermission("claims:adjust")).Returns(true);

        _retro.Setup(r => r.ListChangesAsync(It.IsAny<int>())).ReturnsAsync(
        [
            new FeeScheduleChangeView
            {
                FeeScheduleId = "t:TX FFS:20260101", FeeScheduleName = "TX FFS", Kind = JsonDocument.Parse("1").RootElement,
                PreviousRevision = 2, NewRevision = 3, EffectiveDate = new DateTime(2026, 1, 1), ChangedCodes = ["99213", "99214"],
                LinesRateIncreased = 1, LinesRateDecreased = 1, DefaultForPlanId = "TX-STAR", ChangedAt = DateTime.UtcNow,
            },
        ]);
        _retro.Setup(r => r.ListImpactRunsAsync(It.IsAny<int>())).ReturnsAsync([]);
        _retro.Setup(r => r.ListReprocessingRunsAsync(It.IsAny<int>())).ReturnsAsync([]);
        _retro.Setup(r => r.ListImpactResultsAsync("imp-1", null, It.IsAny<int>())).ReturnsAsync(
        [
            new RetroRateImpactResultView { ClaimId = "c1", ClaimNumber = "CLM-1", Direction = "Underpaid", Match = "Provenance", Paid = 80m, Repriced = 140m, NetPaidDelta = 60m },
            new RetroRateImpactResultView { ClaimId = "c3", ClaimNumber = "CLM-3", Direction = "Overpaid", Match = "Provenance", Paid = 100m, Repriced = 85m, NetPaidDelta = -15m },
        ]);
    }

    private static RetroRateReprocessingRunView Run(string status, string createdBy = "adjuster-2") => new()
    {
        Id = "rp-1", ImpactRunId = "imp-1", FeeScheduleName = "TX FFS", Revision = 3, Status = status, CreatedBy = createdBy,
        ClaimsToAdjust = 2, PlannedAdditionalPayment = 120m,
        SkippedByAction = new() { ["SkipRecoupmentNotOptedIn"] = 1 },
        Items =
        [
            new RetroRateReprocessingItemView { ClaimId = "c1", ClaimNumber = "CLM-1", Direction = "Underpaid", NetPaidDelta = 60m, Action = "PayDifference", Outcome = "Pending" },
            new RetroRateReprocessingItemView { ClaimId = "c3", ClaimNumber = "CLM-3", Direction = "Overpaid", NetPaidDelta = -15m, Action = "SkipRecoupmentNotOptedIn", Outcome = "Skipped" },
        ],
    };

    private static void OpenTab(IRenderedComponent<RetroRateReprocessing> cut, string text)
    {
        cut.WaitForAssertion(() => cut.FindAll(".mud-tab").Should().Contain(t => t.TextContent.Contains(text)));
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains(text)).Click();
    }

    [Fact]
    public void Lists_fee_schedule_changes_with_their_codes()
    {
        var cut = RenderComponent<RetroRateReprocessing>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=change-row]").TextContent
            .Should().Contain("TX FFS").And.Contain("Replaced").And.Contain("rev 2 to 3").And.Contain("2 codes").And.Contain("TX-STAR"));
    }

    [Fact]
    public void Measuring_a_change_runs_the_impact_and_shows_its_claims()
    {
        _retro.Setup(r => r.RunImpactAsync("t:TX FFS:20260101", 3)).ReturnsAsync(new RetroRateResult<RetroRateImpactRunView> { Value = Impact });
        var cut = RenderComponent<RetroRateReprocessing>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=measure-button]"));

        cut.Find("[data-testid=measure-button]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=impact-result-row]").Should().HaveCount(2));
        cut.Find("[data-testid=impact-underpaid]").TextContent.Should().Contain("2").And.Contain("$120.00");
        cut.Find("[data-testid=impact-net]").TextContent.Should().Contain("$105.00");
        cut.Find("[data-testid=impact-note]").TextContent.Should().Contain("Nothing was changed");
        _retro.Verify(r => r.RunImpactAsync("t:TX FFS:20260101", 3), Times.Once);
    }

    [Fact]
    public void A_refused_impact_run_shows_the_reason()
    {
        _retro.Setup(r => r.RunImpactAsync(It.IsAny<string>(), It.IsAny<long?>()))
            .ReturnsAsync(new RetroRateResult<RetroRateImpactRunView> { Error = "No recorded change to fee schedule." });
        var cut = RenderComponent<RetroRateReprocessing>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=measure-button]"));

        cut.Find("[data-testid=measure-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=retro-error]").TextContent.Should().Contain("No recorded change"));
    }

    [Fact]
    public void Proposing_from_an_impact_run_shows_the_plan_and_what_is_skipped()
    {
        _retro.Setup(r => r.ListImpactRunsAsync(It.IsAny<int>())).ReturnsAsync([Impact]);
        _retro.Setup(r => r.ProposeAsync("imp-1")).ReturnsAsync(new RetroRateResult<RetroRateReprocessingRunView> { Value = Run("Proposed", createdBy: "adjuster-1") });
        var cut = RenderComponent<RetroRateReprocessing>();
        OpenTab(cut, "Impact runs");
        cut.WaitForAssertion(() => cut.Find("[data-testid=impact-run-row]"));
        cut.Find("[data-testid=impact-run-row]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=propose-button]"));

        cut.Find("[data-testid=propose-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=reprocessing-plan]").TextContent.Should().Contain("2 claim(s) to adjust").And.Contain("$120.00"));
        cut.Find("[data-testid=reprocessing-skipped]").TextContent.Should().Contain("not opted in to recoupment");
        // The proposer can't approve their own run.
        cut.Find("[data-testid=approve-button]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Someone_else_approves_then_executes()
    {
        _retro.Setup(r => r.ListReprocessingRunsAsync(It.IsAny<int>())).ReturnsAsync([Run("Proposed")]);
        _retro.Setup(r => r.ApproveAsync("rp-1")).ReturnsAsync(new RetroRateResult<RetroRateReprocessingRunView> { Value = Run("Approved") });
        var executed = Run("Completed");
        executed.CountsByOutcome = new() { ["AdjustmentCreated"] = 2, ["Skipped"] = 1 };
        _retro.Setup(r => r.ExecuteAsync("rp-1")).ReturnsAsync(new RetroRateResult<RetroRateReprocessingRunView> { Value = executed });
        var cut = RenderComponent<RetroRateReprocessing>();
        OpenTab(cut, "Reprocessing runs");
        cut.WaitForAssertion(() => cut.Find("[data-testid=reprocessing-run-row]"));
        cut.Find("[data-testid=reprocessing-run-row]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=approve-button]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=approve-button]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=execute-button]"));
        cut.Find("[data-testid=execute-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=reprocessing-outcomes]").TextContent.Should().Contain("2 adjustments created"));
        cut.Find("[data-testid=reprocessing-status]").TextContent.Should().Contain("Completed");
        cut.FindAll("[data-testid=reprocessing-item-row]").Should().HaveCount(2);
    }

    [Fact]
    public void Read_error_takes_claims_service_error_field()
    {
        RetroRateService.ReadError("{\"error\":\"Someone other than the proposer must approve.\"}")
            .Should().Be("Someone other than the proposer must approve.");
        RetroRateService.ReadError("not json").Should().BeNull();
    }
}
