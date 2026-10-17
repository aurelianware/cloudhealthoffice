using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

internal static class WorkItems
{
    public static WorkItemView Claim(string claimId, string? assignedTo = null, string queue = "NCCI / MUE Edit Failures") => new()
    {
        Id = $"tenant-a.claim-{claimId}",
        WorkType = "Claim",
        SourceId = claimId,
        SourceNumber = "CN-" + claimId,
        QueueName = queue,
        PriorityScore = 87.5,
        PriorityReason = "87.5 = deadline 60×1 + ... (due in 12d, age 18d, $1,250)",
        Status = assignedTo is null ? "Open" : "Leased",
        AssignedTo = assignedTo,
        LeaseActive = assignedTo is not null,
        LeaseExpiresAt = assignedTo is null ? null : DateTime.UtcNow.AddMinutes(25),
        DueAt = DateTime.UtcNow.AddDays(12),
        OpenUrl = $"/claims/{claimId}",
        Attributes = new WorkItemAttributesView { PrimaryPendCode = "NCCI", Amount = 1250m },
    };
}

/// <summary>My Work: Get Next opens the item's own screen; nothing to work is a friendly empty state.</summary>
public class MyWorkPageTests : TestContext
{
    private readonly Mock<IWorkManagementService> _work = new();

    public MyWorkPageTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(GatingUsers.With("workqueue:work").Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _work.Setup(w => w.GetMineAsync()).ReturnsAsync(new List<WorkItemView>());
    }

    [Fact]
    public void GetNext_NavigatesToTheItemsOpenUrl()
    {
        _work.Setup(w => w.GetNextAsync(null)).ReturnsAsync(new WorkNextResult
        {
            Item = WorkItems.Claim("c-42", "u-1"), OpenUrl = "/claims/c-42",
        });
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        var cut = RenderComponent<MyWork>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next]"));

        cut.Find("[data-testid=get-next]").Click();

        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/claims/c-42"));
    }

    [Fact]
    public void GetNext_WithNothingToWork_ShowsTheEmptyState_AndStaysOnThePage()
    {
        _work.Setup(w => w.GetNextAsync(null)).ReturnsAsync((WorkNextResult?)null);
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        var cut = RenderComponent<MyWork>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next]"));
        var before = nav.Uri;

        cut.Find("[data-testid=get-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next-empty]").TextContent.Should().Contain("all caught up"));
        nav.Uri.Should().Be(before);
    }

    [Fact]
    public void HeldItems_ShowQueuePriorityReason_AndCanBeReleased()
    {
        var held = WorkItems.Claim("c-7", "u-1", queue: "Coordination of Benefits");
        _work.Setup(w => w.GetMineAsync()).ReturnsAsync(new List<WorkItemView> { held });
        _work.Setup(w => w.ReleaseAsync(held.Id)).ReturnsAsync(held);
        var cut = RenderComponent<MyWork>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CN-c-7"));

        cut.Markup.Should().Contain("Coordination of Benefits");
        cut.Find("[data-testid=priority-reason]").TextContent.Should().Contain("due in 12d");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Release").Click();

        cut.WaitForAssertion(() => _work.Verify(w => w.ReleaseAsync(held.Id), Times.Once));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("CN-c-7"));
    }

    [Fact]
    public void AnOutage_ShowsAnError_NotACrash()
    {
        _work.Setup(w => w.GetMineAsync()).ThrowsAsync(new ServiceUnavailableException("Work Management Service"));

        var cut = RenderComponent<MyWork>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Unable to connect to Work Management Service"));
    }
}

/// <summary>The Work Queues page: work-management queue data and a real supervisor reassign.</summary>
/// <summary>
/// The compact Get Next card on the dashboard: the same button as My Work,
/// with how much the user is holding, and nothing at all for users who
/// can't work queues.
/// </summary>
public class DashboardGetNextCardTests : TestContext
{
    private readonly Mock<IWorkManagementService> _work = new();

    public DashboardGetNextCardTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_work.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _work.Setup(w => w.GetMineAsync()).ReturnsAsync(new List<WorkItemView>());
    }

    private IRenderedComponent<GetNextCard> Render(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions).Object);
        return RenderComponent<GetNextCard>(p => p.Add(x => x.Compact, true));
    }

    [Fact]
    public void Shows_how_much_the_user_holds_and_gets_their_next_item()
    {
        _work.Setup(w => w.GetMineAsync()).ReturnsAsync(new List<WorkItemView> { WorkItems.Claim("c-1", "u-1"), WorkItems.Claim("c-2", "u-1") });
        _work.Setup(w => w.GetNextAsync(null)).ReturnsAsync(new WorkNextResult { Item = WorkItems.Claim("c-9", "u-1"), OpenUrl = "/claims/c-9" });
        var cut = Render("workqueue:work");
        var nav = Services.GetRequiredService<FakeNavigationManager>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next-held]").TextContent.Should().Contain("holding 2 items"));
        cut.Find("[data-testid=get-next]").Click();
        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/claims/c-9"));
    }

    [Fact]
    public void Nothing_waiting_shows_the_caught_up_message()
    {
        _work.Setup(w => w.GetNextAsync(null)).ReturnsAsync((WorkNextResult?)null);

        var cut = Render("workqueue:work");
        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next]"));
        cut.Find("[data-testid=get-next]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next-empty]").TextContent.Should().Contain("all caught up"));
    }

    [Fact]
    public void Users_who_cant_work_queues_see_nothing()
    {
        var cut = Render("claims:read");

        cut.WaitForAssertion(() => cut.Markup.Trim().Should().BeEmpty());
        _work.Verify(w => w.GetMineAsync(), Times.Never);
    }

    [Fact]
    public void A_work_management_outage_doesnt_break_the_dashboard()
    {
        _work.Setup(w => w.GetMineAsync()).ThrowsAsync(new ServiceUnavailableException("Work Management Service"));
        _work.Setup(w => w.GetNextAsync(null)).ThrowsAsync(new ServiceUnavailableException("Work Management Service"));

        var cut = Render("workqueue:work");

        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next-held]").TextContent.Should().Contain("unavailable"));
        cut.Find("[data-testid=get-next]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=get-next-error]").TextContent.Should().Contain("Work Management Service"));
    }
}

public class WorkQueuesReassignTests : TestContext
{
    private readonly Mock<IWorkQueueService> _claimsQueue = new();
    private readonly Mock<IWorkManagementService> _work = new();

    public WorkQueuesReassignTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claimsQueue.Object);
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(Mock.Of<IDialogService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        _claimsQueue.Setup(w => w.GetQueueSummaryAsync()).ReturnsAsync(new WorkQueueSummary { NcciEditFailures = 1 });
        _claimsQueue.Setup(w => w.GetQueueItemsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<WorkQueueItem>
            {
                new() { ClaimId = "c-1", ClaimNumber = "CN-c-1", QueueReason = "NCCI Edit Failure", QueueReasonCode = "NCCI" },
            });
        _work.Setup(w => w.GetQueuesAsync()).ReturnsAsync(new List<WorkQueueCounts>
        {
            new() { Id = "q-ncci", Name = "NCCI / MUE Edit Failures", WorkType = "Claim", IsActive = true, Open = 3, Leased = 1 },
        });
        _work.Setup(w => w.ListAsync(null, null, null, 1, 500)).ReturnsAsync(new WorkItemPage
        {
            Items = [WorkItems.Claim("c-1", "alice")],
        });
        _work.Setup(w => w.GetAssigneesAsync()).ReturnsAsync(["alice", "bob"]);
    }

    private IRenderedComponent<WorkQueues> Render(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions).Object);
        var cut = RenderComponent<WorkQueues>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CN-c-1"));
        return cut;
    }

    [Fact]
    public void QueueCountsAndPriority_ComeFromWorkManagement()
    {
        var cut = Render("claims:read", "claims:work", "workqueue:read", "workqueue:assign");

        cut.WaitForAssertion(() => cut.Find("[data-testid=work-queue-count]").TextContent.Should().Contain("NCCI / MUE Edit Failures: 3 / 1"));
        cut.Find("[data-testid=work-priority]").TextContent.Should().Contain("87.5");
    }

    [Fact]
    public async Task Reassign_CallsTheWorkManagementAssign_AndShowsTheStoredAssignee()
    {
        _work.Setup(w => w.AssignAsync("tenant-a.claim-c-1", "bob")).ReturnsAsync(WorkItems.Claim("c-1", "bob"));
        var cut = Render("claims:read", "claims:work", "workqueue:read", "workqueue:assign");
        var select = cut.FindComponents<MudSelect<string>>()
            .Single(s => s.Instance.UserAttributes.TryGetValue("data-testid", out var id) && id is "reassign");
        select.Instance.Value.Should().Be("alice");

        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("bob"));

        _work.Verify(w => w.AssignAsync("tenant-a.claim-c-1", "bob"), Times.Once);
        _claimsQueue.Verify(w => w.AssignClaimAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never,
            "the claims-service assign stub is no longer used");
        cut.WaitForAssertion(() => cut.FindComponents<MudSelect<string>>()
            .Single(s => s.Instance.UserAttributes.TryGetValue("data-testid", out var id) && id is "reassign")
            .Instance.Value.Should().Be("bob"));
    }

    [Fact]
    public async Task ChoosingUnassigned_ReturnsTheItemToItsQueue()
    {
        _work.Setup(w => w.UnassignAsync("tenant-a.claim-c-1")).ReturnsAsync(WorkItems.Claim("c-1"));
        var cut = Render("claims:read", "claims:work", "workqueue:read", "workqueue:assign");
        var select = cut.FindComponents<MudSelect<string>>()
            .Single(s => s.Instance.UserAttributes.TryGetValue("data-testid", out var id) && id is "reassign");

        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync("(unassigned)"));

        _work.Verify(w => w.UnassignAsync("tenant-a.claim-c-1"), Times.Once);
    }

    [Fact]
    public void WithoutWorkqueueAssign_ThereIsNoReassignControl()
    {
        var cut = Render("claims:read", "claims:work", "workqueue:read", "workqueue:work");

        cut.FindComponents<MudSelect<string>>()
            .Where(s => s.Instance.UserAttributes.TryGetValue("data-testid", out var id) && id is "reassign")
            .Should().BeEmpty();
        _work.Verify(w => w.ListAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void WorkManagementDown_TheClaimsListStillShows_WithAWarning()
    {
        _work.Setup(w => w.GetQueuesAsync()).ThrowsAsync(new ServiceUnavailableException("Work Management Service"));

        var cut = Render("claims:read", "claims:work", "workqueue:read", "workqueue:assign");

        cut.WaitForAssertion(() => cut.Find("[data-testid=work-management-unavailable]"));
        cut.Markup.Should().Contain("CN-c-1");
    }
}

/// <summary>Navigation: My Work for those who pull work, Work Management for those who configure it.</summary>
public class WorkManagementNavTests : TestContext
{
    public WorkManagementNavTests()
    {
        var tenant = new Mock<ITenantContextService>();
        tenant.Setup(t => t.GetCurrentTenantContextAsync()).ReturnsAsync(new TenantContext { TenantId = "tenant-a", TenantName = "Tenant A" });
        tenant.Setup(t => t.GetAvailableTenantsAsync()).ReturnsAsync(new List<TenantSubscription>());
        Services.AddSingleton(tenant.Object);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddTestAuthorization().SetAuthorized("user@test.example");
    }

    private IRenderedComponent<MainLayout> Render(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions).Object);
        var cut = RenderComponent<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Benefit Plans"));
        return cut;
    }

    [Fact]
    public void AnExaminer_SeesMyWork_FirstUnderOperations_ButNotTheAdminPage()
    {
        var cut = Render("workqueue:work", "claims:read");

        var operationsLinks = cut.FindAll("a").Select(a => a.GetAttribute("href")).Where(h => h is not null).ToList();
        operationsLinks.Should().Contain("/my-work");
        operationsLinks.IndexOf("/my-work").Should().BeLessThan(operationsLinks.IndexOf("/dashboard"));
        operationsLinks.Should().NotContain("/admin/work-management");
    }

    [Fact]
    public void ASupervisor_SeesTheAdminPage()
    {
        var cut = Render("workqueue:work", "workqueue:admin");

        cut.FindAll("a[href='/admin/work-management']").Should().NotBeEmpty();
    }

    [Fact]
    public void WithoutWorkqueueWork_MyWorkIsHidden()
    {
        var cut = Render("payments:read");

        cut.FindAll("a[href='/my-work']").Should().BeEmpty();
    }
}

/// <summary>The Work Management admin page renders its configuration and saves a queue.</summary>
public class WorkManagementAdminPageTests : TestContext
{
    private readonly Mock<IWorkManagementService> _work = new();

    public WorkManagementAdminPageTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_work.Object);
        Services.AddSingleton(GatingUsers.With("workqueue:admin").Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _work.Setup(w => w.GetQueueConfigsAsync()).ReturnsAsync(new List<WorkQueueConfig>
        {
            new() { Id = "q-ncci", Name = "NCCI / MUE Edit Failures", Order = 10, Criteria = new WorkQueueCriteria { PendCodes = ["NCCI", "MUE"] } },
            new() { Id = "q-other", Name = "Other / Pending Review", Order = 1000, IsFallback = true },
        });
        _work.Setup(w => w.GetTeamsAsync()).ReturnsAsync(new List<WorkTeamConfig>());
        _work.Setup(w => w.GetMembershipsAsync()).ReturnsAsync(new List<WorkMembershipConfig>
        {
            new() { Id = "m1", UserId = "alice", QueueId = "q-ncci", Rank = 1, AuthorityLimit = 5000m },
        });
    }

    [Fact]
    public void ShowsQueuesWithCriteria_AndMemberships()
    {
        var cut = RenderComponent<WorkManagementAdmin>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("NCCI / MUE Edit Failures"));
        cut.Markup.Should().Contain("pend NCCI/MUE");
        cut.Markup.Should().Contain("Everything no other queue matches");
    }

    [Fact]
    public void EditingAQueue_SavesItsCriteria()
    {
        _work.Setup(w => w.SaveQueueAsync("q-ncci", It.IsAny<WorkQueueInput>())).ReturnsAsync(new WorkQueueConfig());
        var cut = RenderComponent<WorkManagementAdmin>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("NCCI / MUE Edit Failures"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        cut.Find("[data-testid=save-queue]").Click();

        cut.WaitForAssertion(() => _work.Verify(w => w.SaveQueueAsync("q-ncci",
            It.Is<WorkQueueInput>(i => i.Name == "NCCI / MUE Edit Failures" && i.Criteria.PendCodes.SequenceEqual(new[] { "NCCI", "MUE" }))), Times.Once));
    }

    [Fact]
    public void Recompute_ReportsWhatMoved()
    {
        _work.Setup(w => w.RecomputeAsync()).ReturnsAsync(new WorkRecomputeResult { Examined = 4, Rerouted = 2 });
        var cut = RenderComponent<WorkManagementAdmin>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("NCCI / MUE Edit Failures"));

        cut.Find("[data-testid=recompute]").Click();

        cut.WaitForAssertion(() => _work.Verify(w => w.RecomputeAsync(), Times.Once));
    }
}
