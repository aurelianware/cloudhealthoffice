using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Shadow Adjudication page: the plan's switch, running on a loaded claim
/// file, and reading a run's report (why CHO could not decide, where the
/// money differs) with its CSV links.
/// </summary>
public class ShadowAdjudicationPageTests : TestContext
{
    private readonly Mock<IDataDropService> _drops = new();
    private readonly Mock<IShadowAdjudicationClient> _shadow = new();
    private readonly Mock<IUserContextService> _user = new();

    private static readonly ShadowRun Run1 = new()
    {
        Id = "run1", RequestedBy = "cpcds-drop", ImportBatchId = "batch-1", ClaimsRequested = 10, ClaimsCompared = 10,
        MatchRate = 0.5m, UndecidedRate = 0.6m, NetPaidVariance = -42.5m, StartedAtUtc = new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc),
    };

    public ShadowAdjudicationPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_drops.Object);
        Services.AddSingleton(_shadow.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "ops-1", Roles = ["ClaimsSupervisor"] });
        _user.Setup(u => u.HasPermission("claims:import")).Returns(true);

        _drops.Setup(d => d.GetShadowSettingsAsync()).ReturnsAsync(new ShadowAdjudicationSettingsView
        {
            EnabledForDeployment = true, Enabled = true, Running = true, Source = "Stored", MaxClaimsPerRun = 200,
        });
        _drops.Setup(d => d.GetRunsAsync(It.IsAny<int>())).ReturnsAsync(new List<DataDropRunRecord>
        {
            new()
            {
                Id = "drop-1", StartedAt = new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc),
                Steps =
                {
                    new() { Name = "members-and-coverage", Counts = { ["membersCreated"] = 5 } },
                    new() { Name = "claims", Files = { "CPCDS_Claims_20261103.csv" }, ImportBatchId = "batch-1",
                            Counts = { ["created"] = 8, ["unchanged"] = 2, ["rejected"] = 1 } },
                },
            },
        });
        _shadow.Setup(s => s.ListRunsAsync(It.IsAny<int>())).ReturnsAsync(new List<ShadowRun> { Run1 });
        _shadow.Setup(s => s.GetReportAsync("run1", It.IsAny<int>())).ReturnsAsync(new ShadowRunReport
        {
            Run = Run1, ComparableClaims = 10, DecidedClaims = 4,
            CorePaidMore = new() { Claims = 1, Amount = 42.5m },
            ByKind = { new() { Key = "ChoUndecided", Claims = 6 }, new() { Key = "Match", Claims = 2 }, new() { Key = "PaidVariance", Claims = 1, AbsolutePaidVariance = 42.5m } },
            UndecidedByGap = { new() { Key = "NOCONTRACT", Claims = 6, CorePaid = 900m, ExampleClaims = { "C-1", "C-2" } } },
        });
        _shadow.Setup(s => s.ListResultsAsync("run1", null, It.IsAny<int>())).ReturnsAsync(new List<ShadowResult>
        {
            new() { ClaimNumber = "C-9", Kind = "PaidVariance", CoreOutcome = "Paid", ChoOutcome = "Paid", CorePaid = 100m, ChoPaid = 57.5m },
            new() { ClaimNumber = "C-1", Kind = "ChoUndecided", CoreOutcome = "Paid", ChoOutcome = "Pended", ChoPendCode = "NOCONTRACT", ChoPendReason = "No contract for the billing provider" },
            new() { ClaimNumber = "C-5", Kind = "Match", CoreOutcome = "Paid", ChoOutcome = "Paid" },
        });
    }

    [Fact]
    public void The_latest_run_opens_with_its_report_gaps_and_differences()
    {
        var cut = RenderComponent<ShadowAdjudication>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-report]").TextContent.Should().Contain("40%").And.Contain("$42.50"));
        cut.Find("[data-testid=shadow-undecided]").TextContent.Should().Contain("NOCONTRACT").And.Contain("$900.00").And.Contain("C-1");
        cut.Find("[data-testid=shadow-by-kind]").TextContent.Should().Contain("CHO could not decide").And.Contain("Paid differs");
        var results = cut.Find("[data-testid=shadow-results]").TextContent;
        results.Should().Contain("C-9").And.Contain("No contract for the billing provider").And.NotContain("C-5");
        cut.Find("[data-testid=shadow-csv]").GetAttribute("href").Should().Be("/shadow-adjudication/runs/run1/discrepancies.csv");
        cut.Find("[data-testid=shadow-csv-undecided]").GetAttribute("href").Should().EndWith("?includeUndecided=true");
    }

    [Fact]
    public void A_loaded_claim_file_can_be_shadowed_from_the_page()
    {
        var run2 = new ShadowRun { Id = "run2", ClaimsCompared = 10, StartedAtUtc = DateTime.UtcNow };
        _shadow.Setup(s => s.RunBatchAsync("batch-1")).ReturnsAsync(DataDropChange<ShadowRun>.Ok(run2));
        _shadow.Setup(s => s.GetReportAsync("run2", It.IsAny<int>())).ReturnsAsync(new ShadowRunReport { Run = run2, ComparableClaims = 10 });

        var cut = RenderComponent<ShadowAdjudication>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-batches]").TextContent.Should().Contain("CPCDS_Claims_20261103.csv").And.Contain("10"));

        cut.Find("[data-testid=shadow-run-batch]").Click();

        cut.WaitForAssertion(() => _shadow.Verify(s => s.GetReportAsync("run2", It.IsAny<int>()), Times.Once));
        _shadow.Verify(s => s.RunBatchAsync("batch-1"), Times.Once);
    }

    [Fact]
    public void When_the_deployment_has_shadow_off_the_page_says_so_and_runs_are_disabled()
    {
        _drops.Setup(d => d.GetShadowSettingsAsync()).ReturnsAsync(new ShadowAdjudicationSettingsView
        {
            EnabledForDeployment = false, Enabled = true, Running = false, Source = "Configuration",
        });

        var cut = RenderComponent<ShadowAdjudication>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-status-text]").TextContent.Should().Contain("off in this deployment"));
        cut.Find("[data-testid=shadow-run-batch]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Claim_batches_come_from_drop_claim_steps_newest_first_once_each()
    {
        var older = new DataDropRunRecord
        {
            StartedAt = new DateTime(2026, 11, 1), Steps = { new() { Name = "claims", ImportBatchId = "b-old", Counts = { ["created"] = 3 } } },
        };
        var newer = new DataDropRunRecord
        {
            StartedAt = new DateTime(2026, 11, 2),
            Steps = { new() { Name = "claims", ImportBatchId = "b-new", Files = { "c.csv" } }, new() { Name = "authorizations-278", ImportBatchId = "x" } },
        };

        var batches = ShadowAdjudication.ClaimBatches([older, newer]);

        batches.Select(b => b.ImportBatchId).Should().Equal("b-new", "b-old");
        batches[1].Claims.Should().Be(3);
    }
}
