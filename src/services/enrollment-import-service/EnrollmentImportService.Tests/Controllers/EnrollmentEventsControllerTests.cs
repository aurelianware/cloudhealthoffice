using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using EnrollmentImportService.Tests.Services;
using Microsoft.AspNetCore.Mvc;

namespace EnrollmentImportService.Tests.Controllers;

public class EnrollmentEventsControllerTests
{
    private static (EnrollmentEventsController ctl, InMemoryEnrollmentEventRepository repo) Build()
    {
        var repo = new InMemoryEnrollmentEventRepository();
        var ctl = new EnrollmentEventsController(repo);
        return (ctl, repo);
    }

    private static EnrollmentEvent Make(
        string memberId, EnrollmentEventType type, int version, DateTime occurredAt) => new()
    {
        TenantId = "t1",
        MemberId = memberId,
        EventId = $"e-{memberId}-{version}",
        EventType = type,
        Version = version,
        OccurredAt = occurredAt,
        SourceBatchId = "B-1",
        TransactionId = $"T-{version}",
        MaintenanceType = "021"
    };

    [Fact]
    public async Task List_ReturnsEvents_NewestFirst()
    {
        var (ctl, repo) = Build();
        await repo.AppendAsync(Make("M-1", EnrollmentEventType.Enrolled, 1, DateTime.UtcNow.AddDays(-2)));
        await repo.AppendAsync(Make("M-1", EnrollmentEventType.PlanChanged, 2, DateTime.UtcNow.AddDays(-1)));

        var resp = await ctl.List("M-1", "t1");
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var page = ok.Value.Should().BeOfType<EnrollmentEventListResponse>().Subject;
        page.Items.Should().HaveCount(2);
        page.Items[0].Version.Should().Be(2);
    }

    [Fact]
    public async Task List_FiltersByType()
    {
        var (ctl, repo) = Build();
        await repo.AppendAsync(Make("M-1", EnrollmentEventType.Enrolled, 1, DateTime.UtcNow));
        await repo.AppendAsync(Make("M-1", EnrollmentEventType.PlanChanged, 2, DateTime.UtcNow));

        var resp = await ctl.List("M-1", "t1", type: "PlanChanged");
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var page = ok.Value.Should().BeOfType<EnrollmentEventListResponse>().Subject;
        page.Items.Should().ContainSingle();
        page.Items[0].EventType.Should().Be(EnrollmentEventType.PlanChanged);
    }

    [Fact]
    public async Task List_UnknownType_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        var resp = await ctl.List("M-1", "t1", type: "Bogus");
        resp.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_MissingTenant_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        var resp = await ctl.List("M-1", tenantId: "");
        resp.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task List_FiltersByDateWindow()
    {
        var (ctl, repo) = Build();
        var now = DateTime.UtcNow;
        await repo.AppendAsync(Make("M-1", EnrollmentEventType.Enrolled, 1, now.AddDays(-10)));
        await repo.AppendAsync(Make("M-1", EnrollmentEventType.PlanChanged, 2, now.AddDays(-1)));

        var resp = await ctl.List("M-1", "t1", from: now.AddDays(-5));
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var page = ok.Value.Should().BeOfType<EnrollmentEventListResponse>().Subject;
        page.Items.Should().ContainSingle();
        page.Items[0].Version.Should().Be(2);
    }
}
