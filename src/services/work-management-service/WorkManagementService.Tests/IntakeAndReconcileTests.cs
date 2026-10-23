using CloudHealthOffice.Events;
using WorkManagementService.HostedServices;
using WorkManagementService.Models;
using WorkManagementService.Services;
using WorkManagementService.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace WorkManagementService.Tests;

/// <summary>Event consumption: idempotent, order-safe, and every transition audited.</summary>
public class IntakeTests
{
    private readonly WorkTestKit _kit = new();

    private Task<WorkItem?> Item(string claimId) => _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId(claimId));

    [Fact]
    public async Task WorkNeeded_CreatesARoutedScoredItem_InTheDefaultQueue()
    {
        var outcome = await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1", "COB", 2_500m));

        outcome.Should().Be(IntakeOutcome.Created);
        var item = (await Item("c1"))!;
        item.QueueId.Should().Be(WorkTestKit.QueueId("claim.cob"));
        item.Status.Should().Be(WorkItemStatus.Open);
        item.SourceRef.Should().Be("claim:c1");
        item.SourceNumber.Should().Be("CN-c1");
        item.PriorityScore.Should().BeGreaterThan(0);
        item.PriorityReason.Should().NotBeNullOrWhiteSpace();
        item.DueAt.Should().NotBeNull();
        item.Attributes.Amount.Should().Be(2_500m);
        item.Version.Should().Be(1);
    }

    [Fact]
    public async Task ADuplicateEvent_IsANoOp()
    {
        var evt = _kit.Needed("c1");
        await _kit.Intake().ApplyClaimEventAsync(evt);

        (await _kit.Intake().ApplyClaimEventAsync(evt)).Should().Be(IntakeOutcome.Ignored);
        (await Item("c1"))!.Version.Should().Be(1);
    }

    [Fact]
    public async Task ALateNeeded_AfterResolved_DoesNotReopen()
    {
        var needed = _kit.Needed("c1");
        var resolved = _kit.Resolved("c1"); // newer version

        await _kit.Intake().ApplyClaimEventAsync(needed);
        (await _kit.Intake().ApplyClaimEventAsync(resolved)).Should().Be(IntakeOutcome.Completed);
        (await _kit.Intake().ApplyClaimEventAsync(needed)).Should().Be(IntakeOutcome.Ignored);

        (await Item("c1"))!.Status.Should().Be(WorkItemStatus.Done);
    }

    [Fact]
    public async Task ResolvedArrivingFirst_LeavesATombstone_SoTheOlderNeededIsIgnored()
    {
        var needed = _kit.Needed("c1");
        var resolved = _kit.Resolved("c1");

        await _kit.Intake().ApplyClaimEventAsync(resolved);
        (await _kit.Intake().ApplyClaimEventAsync(needed)).Should().Be(IntakeOutcome.Ignored);

        (await Item("c1"))!.Status.Should().Be(WorkItemStatus.Done);
    }

    [Fact]
    public async Task Resolved_CompletesALeasedItem_ClearsTheLease_AndAudits()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));
        await _kit.Distribution().GetNextAsync(new TestActor("alice"), null);

        (await _kit.Intake().ApplyClaimEventAsync(_kit.Resolved("c1", "Deny"))).Should().Be(IntakeOutcome.Completed);

        var item = (await Item("c1"))!;
        item.Status.Should().Be(WorkItemStatus.Done);
        item.AssignedTo.Should().BeNull();
        item.Resolution.Should().Be("Deny");
        item.CompletedBy.Should().Be("examiner-1");
        (await _kit.Store.ListAuditAsync(WorkTestKit.Tenant, item.Id))
            .Should().Contain(a => a.Action == WorkAuditActions.Completed && a.FromUser == "alice");
    }

    [Fact]
    public async Task RePendedAfterResolution_Reopens_InTheNewQueue()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1", "NCCI"));
        await _kit.Intake().ApplyClaimEventAsync(_kit.Resolved("c1"));

        (await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1", "COB"))).Should().Be(IntakeOutcome.Reopened);

        var item = (await Item("c1"))!;
        item.Status.Should().Be(WorkItemStatus.Open);
        item.QueueId.Should().Be(WorkTestKit.QueueId("claim.cob"));
        item.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task ChangedPends_OnAnOpenItem_Reroute_AndKeepTheLease()
    {
        await _kit.JoinAsync("alice", "claim.ncci");
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1", "NCCI"));
        await _kit.Distribution().GetNextAsync(new TestActor("alice"), null);

        (await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1", "MEDREVIEW"))).Should().Be(IntakeOutcome.Rerouted);

        var item = (await Item("c1"))!;
        item.QueueId.Should().Be(WorkTestKit.QueueId("claim.medreview"));
        item.AssignedTo.Should().Be("alice");
        item.Status.Should().Be(WorkItemStatus.Leased);
    }

    [Fact]
    public async Task SameContent_NewerVersion_DoesNotChurnTheItem()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));

        (await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"))).Should().Be(IntakeOutcome.Ignored);
        (await Item("c1"))!.Version.Should().Be(1);
    }

    [Fact]
    public async Task OutOfOrderBurst_ConvergesOnTheNewestEvent()
    {
        var events = new List<ClaimWorkEvent>
        {
            _kit.Needed("c1", "NCCI"),
            _kit.Resolved("c1"),
            _kit.Needed("c1", "COB"),
            _kit.Resolved("c1", "Deny"),
            _kit.Needed("c1", "MEDREVIEW"), // newest
        };
        var shuffled = events.OrderBy(_ => Guid.NewGuid()).ToList();

        foreach (var e in shuffled.Concat(shuffled)) // and every one delivered twice
            await _kit.Intake().ApplyClaimEventAsync(e);

        var item = (await Item("c1"))!;
        item.Status.Should().Be(WorkItemStatus.Open);
        item.QueueId.Should().Be(WorkTestKit.QueueId("claim.medreview"));
        item.SourceVersion.Should().Be(events[^1].SourceVersion);
    }

    [Fact]
    public async Task ParallelDeliveries_OfOneClaim_ProduceOneItem()
    {
        var evt = _kit.Needed("c1");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => _kit.Intake().ApplyClaimEventAsync(evt))));

        outcomes.Count(o => o == IntakeOutcome.Created).Should().Be(1);
        outcomes.Where(o => o != IntakeOutcome.Created).Should().OnlyContain(o => o == IntakeOutcome.Ignored);
    }

    [Fact]
    public async Task UnknownEventType_IsRejected()
    {
        var evt = _kit.Needed("c1");
        evt.EventType = "claim.work.maybe";

        await _kit.Invoking(k => k.Intake().ApplyClaimEventAsync(evt)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Consumer_AppliesAPayload_AndSkipsPoisonMessages()
    {
        var services = new ServiceCollection()
            .AddSingleton(_kit.Store)
            .AddSingleton<TimeProvider>(_kit.Clock)
            .AddSingleton(_kit.Seeds)
            .AddSingleton<IWorkEntitlementDirectory>(_kit.Entitlements)
            .AddLogging()
            .AddScoped<WorkConfigService>()
            .AddScoped<WorkIntakeService>()
            .BuildServiceProvider();
        var consumer = new ClaimWorkEventConsumer(services, new ConfigurationBuilder().Build(), NullLogger<ClaimWorkEventConsumer>.Instance);

        await consumer.ProcessAsync(System.Text.Json.JsonSerializer.Serialize(_kit.Needed("c1"),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }), default);
        await consumer.ProcessAsync("{not json", default);
        await consumer.ProcessAsync("{}", default);

        (await Item("c1")).Should().NotBeNull();
    }
}

/// <summary>Reconcile against claims-service fixes drift in both directions.</summary>
public class ReconcileTests
{
    private readonly WorkTestKit _kit = new();
    private readonly FakeClaimsWorkSource _claims = new();

    private Task<ReconcileResult> Reconcile(int pageSize = 2) => _kit.Intake().ReconcileClaimsAsync(WorkTestKit.Tenant, _claims, pageSize);

    [Fact]
    public async Task CreatesMissingItems_ClosesItemsNoLongerPended_AcrossPages()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("stale"));   // resolved, event lost
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("kept"));
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        _claims.ObservedAt = _kit.Now;
        _claims.Pended.AddRange([_kit.Needed("kept"), _kit.Needed("missing-1", "COB"), _kit.Needed("missing-2", "DUPLICATE")]);

        var result = await Reconcile(pageSize: 2);

        result.Succeeded.Should().BeTrue();
        result.Pended.Should().Be(3);
        result.Created.Should().Be(2);
        result.Closed.Should().Be(1);
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("stale")))!.Status.Should().Be(WorkItemStatus.Done);
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("missing-2")))!.QueueId
            .Should().Be(WorkTestKit.QueueId("claim.duplicate"));
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("kept")))!.Status.Should().Be(WorkItemStatus.Open);
    }

    [Fact]
    public async Task ReopensAnItemWhoseClaimIsPendedAgain()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));
        await _kit.Intake().ApplyClaimEventAsync(_kit.Resolved("c1"));
        _kit.Clock.Advance(TimeSpan.FromMinutes(1));
        _claims.ObservedAt = _kit.Now;
        _claims.Pended.Add(_kit.Needed("c1", "COB"));

        var result = await Reconcile();

        result.Reopened.Should().Be(1);
    }

    [Fact]
    public async Task IsIdempotent()
    {
        _claims.Pended.AddRange([_kit.Needed("a"), _kit.Needed("b")]);
        await Reconcile();
        _claims.ObservedAt = _kit.Now.AddSeconds(1);

        var second = await Reconcile();

        second.Created.Should().Be(0);
        second.Closed.Should().Be(0);
        second.Unchanged.Should().Be(2);
    }

    [Fact]
    public async Task ClaimsServiceDown_ChangesNothing()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));
        _claims.Fail = true;

        var result = await Reconcile();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("claims-service");
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("c1")))!.Status.Should().Be(WorkItemStatus.Open);
    }

    [Fact]
    public async Task AnItemNewerThanTheReconcile_IsNotClosed()
    {
        // An event that arrived after the reconcile started (its claim pended
        // after claims-service answered) must survive the close pass.
        _claims.ObservedAt = _kit.Now;
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("late", version: _kit.Now.AddMinutes(5).Ticks));

        var result = await Reconcile();

        result.Closed.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "", true)]
    [InlineData(null, "kafka:9092", false)]
    [InlineData(true, "kafka:9092", true)]
    [InlineData(false, "", false)]
    public void PeriodicReconcile_IsOnByDefault_ExactlyWhenThereIsNoKafka(bool? setting, string bootstrap, bool expected)
    {
        var options = new WorkManagementOptions { Reconcile = { Enabled = setting } };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Kafka:BootstrapServers"] = bootstrap })
            .Build();

        WorkMaintenanceService.ReconcileEnabled(options, config).Should().Be(expected);
    }
}

/// <summary>Recompute re-routes and re-scores open items after a configuration change.</summary>
public class RecomputeTests
{
    private readonly WorkTestKit _kit = new();

    [Fact]
    public async Task ANewQueue_TakesMatchingOpenItems_OnRecompute()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("big", amount: 80_000m));
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("small", amount: 50m));
        var created = await _kit.Config().CreateQueueAsync(WorkTestKit.Tenant, new QueueInput
        {
            Name = "High dollar", Order = 1, Criteria = new WorkQueueCriteria { MinAmount = 10_000m },
        }, "admin");
        created.Error.Should().BeNull();

        var result = await _kit.Intake().RecomputeAsync(WorkTestKit.Tenant);

        result.Rerouted.Should().Be(1);
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("big")))!.QueueId.Should().Be(created.Value!.Id);
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("small")))!.QueueId
            .Should().Be(WorkTestKit.QueueId("claim.ncci"));
    }

    [Fact]
    public async Task Rescore_RaisesPriority_AsItemsAge()
    {
        await _kit.Intake().ApplyClaimEventAsync(_kit.Needed("c1"));
        var before = (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("c1")))!.PriorityScore;
        _kit.Clock.Advance(TimeSpan.FromDays(10));

        var result = await _kit.Intake().RecomputeAsync(WorkTestKit.Tenant, reroute: false);

        result.Rescored.Should().Be(1);
        (await _kit.Store.GetItemAsync(WorkTestKit.Tenant, WorkTestKit.ItemId("c1")))!.PriorityScore.Should().BeGreaterThan(before);
    }
}
