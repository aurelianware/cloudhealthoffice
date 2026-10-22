using CloudHealthOffice.Infrastructure.Work;
using WorkManagementService.Domain;
using WorkManagementService.Models;
using WorkManagementService.Services;
using WorkManagementService.Tests.Support;

namespace WorkManagementService.Tests;

/// <summary>Enrollment exceptions flow in by reconciling against enrollment-import-service.</summary>
public class EnrollmentReconcileTests
{
    private readonly WorkTestKit _kit = new();
    private readonly FakeEnrollmentWorkSource _source = new();
    private const string T = WorkTestKit.Tenant;

    private Task<ReconcileResult> Reconcile(int pageSize = 2) => _kit.Intake().ReconcileEnrollmentAsync(T, _source, pageSize);

    private EnrollmentWorkSnapshot Exception(string id, string reason, int minutesAgo = 5) => new()
    {
        ExceptionId = id, Number = "EX-" + id.ToUpperInvariant(), ReasonCode = reason, GroupNumber = "GRP1",
        MemberId = "M-" + id, MaintenanceTypeCode = "021",
        CreatedAt = _kit.Now.AddMinutes(-minutesAgo), SourceVersion = _kit.Now.AddMinutes(-minutesAgo).Ticks,
    };

    private static string ItemId(string id) => WorkItem.IdFor(T, WorkType.Enrollment834, id);

    [Fact]
    public async Task EachException_BecomesAnItem_RoutedByItsReason_AcrossPages()
    {
        _source.Items.AddRange([
            Exception("a", "SUBSCRIBER_NOT_FOUND"), Exception("b", "RETRO_BEYOND_LIMIT"),
            Exception("c", "DOWNSTREAM_UNAVAILABLE"), Exception("d", "SOMETHING_NEW")]);

        var result = await Reconcile(pageSize: 3);

        result.Succeeded.Should().BeTrue();
        result.Pended.Should().Be(4);
        result.Created.Should().Be(4);
        async Task<WorkItem> Item(string id) => (await _kit.Store.GetItemAsync(T, ItemId(id)))!;
        (await Item("a")).QueueId.Should().Be(WorkTestKit.QueueId("enrollment.matching"));
        (await Item("b")).QueueId.Should().Be(WorkTestKit.QueueId("enrollment.retro"));
        (await Item("c")).QueueId.Should().Be(WorkTestKit.QueueId("enrollment.retry"));
        (await Item("d")).QueueId.Should().Be(WorkTestKit.QueueId("enrollment.other"), "unknown reasons fall back");
        var a = await Item("a");
        a.WorkType.Should().Be(WorkType.Enrollment834);
        a.SourceNumber.Should().Be("EX-A");
        WorkEntitlements.OpenUrl(a).Should().Be("/enrollment-ops/exceptions/a");
    }

    [Fact]
    public async Task IsIdempotent_AndClosesItemsNoLongerNeedingWork()
    {
        _source.Items.AddRange([Exception("a", "VALIDATION_FAILED"), Exception("b", "VALIDATION_FAILED")]);
        await Reconcile();
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));

        var again = await Reconcile();
        again.Created.Should().Be(0);
        again.Unchanged.Should().Be(2);
        again.Closed.Should().Be(0);

        _source.Items.RemoveAll(s => s.ExceptionId == "b"); // resolved, or held
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        var third = await Reconcile();

        third.Closed.Should().Be(1);
        (await _kit.Store.GetItemAsync(T, ItemId("b")))!.Status.Should().Be(WorkItemStatus.Done);
        (await _kit.Store.GetItemAsync(T, ItemId("a")))!.Status.Should().Be(WorkItemStatus.Open);
    }

    [Fact]
    public async Task AReprocessThatFailsForANewReason_MovesTheItem()
    {
        _source.Items.Add(Exception("a", "DOWNSTREAM_UNAVAILABLE"));
        await Reconcile();
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        _source.Items[0] = Exception("a", "SUBSCRIBER_NOT_FOUND", minutesAgo: 0);

        var result = await Reconcile();

        result.Updated.Should().Be(1);
        (await _kit.Store.GetItemAsync(T, ItemId("a")))!.QueueId.Should().Be(WorkTestKit.QueueId("enrollment.matching"));
    }

    [Fact]
    public async Task EnrollmentServiceDown_ChangesNothing_AndClaimItemsAreNeverTouched()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));
        _source.Items.Add(Exception("a", "VALIDATION_FAILED"));
        await Reconcile();
        _source.Fail = true;

        var result = await Reconcile();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("enrollment-import-service");
        (await _kit.Store.GetItemAsync(T, ItemId("a")))!.Status.Should().Be(WorkItemStatus.Open);

        _source.Fail = false;
        _source.Items.Clear();
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        (await Reconcile()).Closed.Should().Be(1);
        (await _kit.Store.GetItemAsync(T, WorkTestKit.ItemId("c1")))!.Status.Should().Be(WorkItemStatus.Open, "only enrollment items are reconciled here");
    }
}

public sealed class FakeEnrollmentWorkSource : IEnrollmentWorkSource
{
    public List<EnrollmentWorkSnapshot> Items { get; } = new();
    public bool Fail { get; set; }

    public Task<EnrollmentWorkSnapshotPage> GetWorkablePageAsync(string tenantId, int page, int pageSize, CancellationToken ct = default)
    {
        if (Fail) throw new HttpRequestException("connection refused");
        return Task.FromResult(new EnrollmentWorkSnapshotPage
        {
            Page = page, PageSize = pageSize, TotalCount = Items.Count, ObservedAt = DateTime.UtcNow,
            Items = Items.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
        });
    }
}
