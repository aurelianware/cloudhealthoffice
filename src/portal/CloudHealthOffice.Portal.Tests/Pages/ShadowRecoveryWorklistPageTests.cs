using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>Recovery worklist: totals, the findings with their lookback, a reviewer's status change, the lookback settings.</summary>
public class ShadowRecoveryWorklistPageTests : TestContext
{
    private readonly Mock<IShadowFindingsClient> _findings = new();
    private readonly Mock<IUserContextService> _user = new();

    private static ShadowFindingView Over(string claim, decimal amount, string status, string lookback, List<string> next) => new()
    {
        Finding = new ShadowFinding
        {
            Id = $"plan-a:{claim}", ClaimNumber = claim, SourceClaimId = $"Q-{claim}", BillingProviderNpi = "1111111111",
            Direction = "CorePaidMore", Amount = amount, Status = status, Stage = "Pricing",
            Reason = "Pricing: 97110 at the 2026 fee schedule", CorePaidDate = new DateTime(2026, 5, 1),
        },
        LookbackState = lookback, LookbackDeadline = new DateTime(2026, 10, 28), DaysLeft = -26, NextStatuses = next,
    };

    public ShadowRecoveryWorklistPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_findings.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "pi-1", Roles = ["ClaimsSupervisor"] });
        _user.Setup(u => u.HasPermission("claims:import")).Returns(true);
        _user.Setup(u => u.HasPermission("claims:adjust")).Returns(true);

        _findings.Setup(f => f.GetSummaryAsync(It.IsAny<int>())).ReturnsAsync(new ShadowFindingSummary
        {
            Findings = 2,
            Overpayments = new() { Findings = 2, Amount = 130m },
            Confirmed = new() { Findings = 1, Amount = 100m },
            Recovered = new() { Findings = 0 },
            PastLookback = new() { Findings = 1, Amount = 30m },
            LookbackUnknown = new() { Findings = 1, Amount = 100m },
            ByProvider = { new() { BillingProviderNpi = "1111111111", ContractNumber = "K-7", Findings = 2, CorePaidMore = 130m, OpenOverpayments = 130m, PastLookback = 1 } },
        });
        _findings.Setup(f => f.ListAsync(null, null, null, It.IsAny<int>())).ReturnsAsync(new List<ShadowFindingView>
        {
            Over("C1", 100m, "Approved", "Unknown", ["InRecovery", "Recovered", "Rejected"]),
            Over("C2", 30m, "New", "Past", ["Approved", "Rejected"]),
        });
        _findings.Setup(f => f.GetLookbackAsync()).ReturnsAsync(new ShadowRecoveryLookback
        {
            DefaultDays = 365, Providers = { new() { Npi = "1111111111", ContractNumber = "K-7", Days = 180 } },
        });
    }

    [Fact]
    public void Totals_findings_lookback_and_providers_show()
    {
        var cut = RenderComponent<ShadowRecoveryWorklist>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=findings-tiles]").TextContent.Should().Contain("$130.00").And.Contain("$100.00"));
        cut.Find("[data-testid=findings-table]").TextContent.Should().Contain("Q-C1").And.Contain("Core paid more").And.Contain("97110");
        cut.FindAll("[data-testid=finding-lookback]").Select(e => e.TextContent).Should().Contain("Past (10/28/2026)").And.Contain("Not entered");
        cut.Find("[data-testid=findings-lookback-unknown]").TextContent.Should().Contain("no recoupment");
        cut.Find("[data-testid=findings-providers]").TextContent.Should().Contain("K-7");
        cut.Find("[data-testid=findings-export]").GetAttribute("href").Should().Be("/shadow-adjudication/findings/export.csv?status=Approved");
        cut.FindAll("textarea").Select(t => t.GetAttribute("value") ?? t.TextContent).Should().Contain(v => v.Contains("1111111111, K-7, 180"));
    }

    [Fact]
    public void A_reviewer_moves_a_finding_and_the_list_reloads()
    {
        _findings.Setup(f => f.SetStatusAsync("plan-a:C2", It.Is<ShadowFindingStatusRequest>(r => r.Status == "Approved" && r.RecoveredAmount == null)))
            .ReturnsAsync(DataDropChange<ShadowFindingView>.Ok(Over("C2", 30m, "Approved", "Past", [])));

        var cut = RenderComponent<ShadowRecoveryWorklist>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=finding-review]").Count.Should().Be(2));
        cut.FindAll("[data-testid=finding-review]").Last().Click();
        cut.Find("[data-testid=finding-save]").Click();

        cut.WaitForAssertion(() => _findings.Verify(f => f.SetStatusAsync("plan-a:C2", It.IsAny<ShadowFindingStatusRequest>()), Times.Once));
        _findings.Verify(f => f.GetSummaryAsync(It.IsAny<int>()), Times.Exactly(2));
    }

    [Fact]
    public void Without_claims_adjust_there_is_nothing_to_review_or_save()
    {
        _user.Setup(u => u.HasPermission("claims:adjust")).Returns(false);

        var cut = RenderComponent<ShadowRecoveryWorklist>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=findings-table]"));
        cut.FindAll("[data-testid=finding-review]").Should().BeEmpty();
        cut.FindAll("[data-testid=lookback-save]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("1111111111, K-7, 180\n2222222222, 90", 2, 0)]
    [InlineData("1111111111, K-7", 0, 1)]
    [InlineData("", 0, 0)]
    public void Provider_lookback_lines_parse(string text, int providers, int errors)
    {
        var (p, e) = ShadowRecoveryWorklist.ParseProviders(text);
        p.Should().HaveCount(providers);
        e.Should().HaveCount(errors);
    }
}
