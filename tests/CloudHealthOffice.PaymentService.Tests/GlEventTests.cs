using System.Text.Json;
using CloudHealthOffice.Finance.Contracts;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// GL source events: written with the run's outcome, deterministic, money only; delivered
/// at least once by the dispatcher, backing off on failure and never dropped.
/// </summary>
public class GlEventTests
{
    private const string Npi = "1111111111";
    private const string CheckNpi = "2222222222";

    [Fact]
    public async Task An_executed_run_carries_its_accrual_event_in_the_same_write()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Partner(CheckNpi, "TP-B");
        h.Accounts.Eft(Npi);
        h.Accounts.NoEft(CheckNpi);

        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m), FfsRunHarness.Claim("c2", CheckNpi, 50m));

        var stored = (await h.Runs.GetByIdAsync(run.Id))!;
        var message = Assert.Single(stored.GlOutbox);
        Assert.Equal(GlEventTypes.PaymentRunExecuted, message.Type);
        Assert.Null(message.PublishedAt);
        Assert.Equal(GlEventTypes.EventIdFor(run.TenantId, $"payment-run:{run.Id}", GlEventTypes.PaymentRunExecuted), message.EventId);
        var payload = JsonSerializer.Deserialize<PaymentRunExecutedEvent>(message.PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((run.Id, "Completed", 150m, 0m), (payload.PaymentRunId, payload.RunStatus, payload.TotalNetAmount, payload.TotalReceivableOffsetAmount));
        Assert.Equal(new[] { ("ACH", 100m), ("CHK", 50m) },
            payload.Payments.OrderBy(p => p.NetAmount).Reverse().Select(p => (p.PaymentMethod, p.NetAmount)));
        Assert.Equal(run.PaymentDate.Date, payload.PaymentDate);
        Assert.DoesNotContain("111122223333", message.PayloadJson); // no bank numbers
    }

    [Fact]
    public void A_failed_run_that_issued_payments_still_accrues_them_and_attaching_twice_adds_nothing()
    {
        var run = new PaymentRun { Id = "run-f", TenantId = "t1", PaymentRunNumber = "PR-F", Status = PaymentRunStatus.Failed, ExecutedBy = "approver-1" };
        var issued = new List<Payment>
        {
            new() { Id = "p1", CheckNumber = "0000000001", PayeeNPI = Npi, PaymentMethod = "ACH", TotalPaymentAmount = 80m,
                ReceivableOffsets = { new ReceivableOffset { ReceivableId = "r1", Amount = 20m, AdjustmentCode = "FB" } } },
        };

        GlEventOutbox.AttachPaymentRunExecuted(run, issued, DateTime.UtcNow);
        GlEventOutbox.AttachPaymentRunExecuted(run, issued, DateTime.UtcNow);
        GlEventOutbox.AttachPaymentRunExecuted(new PaymentRun(), new List<Payment>(), DateTime.UtcNow);

        var payload = JsonSerializer.Deserialize<PaymentRunExecutedEvent>(Assert.Single(run.GlOutbox).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal(("Failed", 80m, 20m), (payload.RunStatus, payload.TotalNetAmount, payload.TotalReceivableOffsetAmount));
    }

    [Fact]
    public async Task A_run_whose_completed_save_fails_reports_the_status_it_was_saved_with()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        // The write recording Completed fails; the run is then saved as Failed.
        h.Runs.FailUpdateWhen = r => r.Status == PaymentRunStatus.Completed ? new TimeoutException("database") : null;

        await Assert.ThrowsAsync<TimeoutException>(() => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m)));

        var stored = Assert.Single(await h.Runs.SearchAsync(DateTime.MinValue, DateTime.MaxValue));
        Assert.Equal(PaymentRunStatus.Failed, stored.Status);
        var payload = JsonSerializer.Deserialize<PaymentRunExecutedEvent>(Assert.Single(stored.GlOutbox).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal(("Failed", 100m), (payload.RunStatus, payload.TotalNetAmount));
    }

    [Fact]
    public void A_published_accrual_is_never_replaced()
    {
        var run = new PaymentRun { Id = "run-p", TenantId = "t1", PaymentRunNumber = "PR-P", Status = PaymentRunStatus.Completed };
        var issued = new List<Payment> { new() { Id = "p1", CheckNumber = "1", PayeeNPI = Npi, PaymentMethod = "ACH", TotalPaymentAmount = 10m } };
        GlEventOutbox.AttachPaymentRunExecuted(run, issued, DateTime.UtcNow);
        run.GlOutbox[0].PublishedAt = DateTime.UtcNow;
        run.Status = PaymentRunStatus.Failed;

        GlEventOutbox.AttachPaymentRunExecuted(run, issued, DateTime.UtcNow, replaceUnpublished: true);

        Assert.Equal("Completed", JsonSerializer.Deserialize<PaymentRunExecutedEvent>(Assert.Single(run.GlOutbox).PayloadJson, GlEventTypes.Json)!.RunStatus);
    }

    [Fact]
    public void A_reversal_run_reports_the_recouped_amounts_as_positive()
    {
        var run = new ReversalRun { Id = "rr-1", TenantId = "t1", ReversalRunNumber = "RR-1", Status = ReversalRunStatus.Completed };

        GlEventOutbox.AttachReversalRunExecuted(run, new List<Payment>
        {
            new() { Id = "rp1", CheckNumber = "0000000009", PayeeNPI = Npi, IsReversal = true, TotalPaymentAmount = -75.25m },
        }, DateTime.UtcNow);

        var payload = JsonSerializer.Deserialize<ReversalRunExecutedEvent>(Assert.Single(run.GlOutbox).PayloadJson, GlEventTypes.Json)!;
        Assert.Equal((75.25m, 75.25m), (payload.Reversals.Single().Amount, payload.TotalAmount));
    }

    private sealed class FakeStore : IGlOutboxStore
    {
        public List<PendingGlEvent> Pending { get; } = new();
        public List<string> Published { get; } = new();
        public List<(string EventId, string Error, DateTime Next)> Failed { get; } = new();

        public Task<IReadOnlyList<PendingGlEvent>> ListDueAsync(DateTime now, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PendingGlEvent>>(Pending
                .Where(p => p.Message.PublishedAt == null && (p.Message.NextAttemptAt == null || p.Message.NextAttemptAt <= now)).Take(limit).ToList());

        public Task<IReadOnlyList<PendingGlEvent>> ListUnpublishedAsync(string tenantId, int limit = 500, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PendingGlEvent>>(Pending.Where(p => p.TenantId == tenantId && p.Message.PublishedAt == null).ToList());

        public Task MarkPublishedAsync(PendingGlEvent pending, DateTime at, CancellationToken cancellationToken = default)
        {
            pending.Message.PublishedAt = at;
            Published.Add(pending.Message.EventId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(PendingGlEvent pending, string error, DateTime nextAttemptAt, CancellationToken cancellationToken = default)
        {
            pending.Message.PublishAttempts++;
            pending.Message.LastError = error;
            pending.Message.NextAttemptAt = nextAttemptAt;
            Failed.Add((pending.Message.EventId, error, nextAttemptAt));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSink : IGlEventSink
    {
        public List<GlEventEnvelope> Delivered { get; } = new();
        public Func<GlEventEnvelope, Exception?> Fail { get; set; } = _ => null;

        public Task DeliverAsync(GlEventEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (Fail(envelope) is { } error) throw error;
            Delivered.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private static PendingGlEvent Pending(string id, string tenant = "t1", DateTime? created = null) => new(GlOutboxSource.PaymentRun, "run-" + id, tenant,
        new PaymentFileOutboxMessage { EventId = id, Type = GlEventTypes.PaymentRunExecuted, PayloadJson = "{\"x\":1}", CreatedAt = created ?? new DateTime(2026, 5, 1) });

    private static GlEventDispatcher Dispatcher(FakeStore store, FakeSink sink, MutableClock clock)
        => new(store, sink, Options.Create(new GlEventOptions { DispatchEnabled = true, BatchSize = 10 }), clock, NullLogger<GlEventDispatcher>.Instance);

    [Fact]
    public async Task The_dispatcher_delivers_and_marks_each_event_with_its_payload_verbatim()
    {
        var store = new FakeStore();
        store.Pending.AddRange(new[] { Pending("e1"), Pending("e2", tenant: "t2") });
        var sink = new FakeSink();

        Assert.Equal(2, await Dispatcher(store, sink, new MutableClock()).DispatchOnceAsync());

        Assert.Equal(new[] { "e1", "e2" }, store.Published);
        Assert.Equal(("t2", "{\"x\":1}", "payment-service"), (sink.Delivered[1].TenantId, sink.Delivered[1].PayloadJson, sink.Delivered[1].Source));
        Assert.Equal(0, await Dispatcher(store, sink, new MutableClock()).DispatchOnceAsync());
    }

    [Fact]
    public async Task A_failing_event_backs_off_and_is_never_dropped_while_others_go_through()
    {
        var store = new FakeStore();
        store.Pending.AddRange(new[] { Pending("bad"), Pending("good") });
        var sink = new FakeSink { Fail = e => e.EventId == "bad" ? new GlEventDeliveryException("ar-service answered 409 Conflict.") : null };
        var clock = new MutableClock();

        await Dispatcher(store, sink, clock).DispatchOnceAsync();

        Assert.Equal(new[] { "good" }, store.Published);
        var failure = Assert.Single(store.Failed);
        Assert.Equal(("bad", clock.Now.UtcDateTime.AddMinutes(1)), (failure.EventId, failure.Next));
        // Not due yet: not attempted again.
        await Dispatcher(store, sink, clock).DispatchOnceAsync();
        Assert.Single(store.Failed);
        // Due again: the backoff doubles; once ar-service accepts, it is delivered.
        clock.Now = clock.Now.AddMinutes(2);
        await Dispatcher(store, sink, clock).DispatchOnceAsync();
        Assert.Equal(clock.Now.UtcDateTime.AddMinutes(2), store.Failed[1].Next);
        clock.Now = clock.Now.AddMinutes(3);
        sink.Fail = _ => null;
        await Dispatcher(store, sink, clock).DispatchOnceAsync();
        Assert.Contains("bad", store.Published);
        Assert.Empty(await store.ListUnpublishedAsync("t1"));
    }

    [Fact]
    public async Task An_unexpected_exception_is_recorded_without_its_message()
    {
        var store = new FakeStore();
        store.Pending.Add(Pending("e1"));
        var sink = new FakeSink { Fail = _ => new InvalidOperationException("secret detail") };

        await Dispatcher(store, sink, new MutableClock()).DispatchOnceAsync();

        Assert.Equal("Delivery failed (InvalidOperationException).", Assert.Single(store.Failed).Error);
    }
}

/// <summary>The outbox store against a real mongod: across collections and tenants, positional marking.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoGlOutboxStoreTests : IAsyncLifetime
{
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public MongoGlOutboxStoreTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("payment_glout");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static PaymentFileOutboxMessage Message(string id, int minute, DateTime? published = null) => new()
    {
        EventId = id, Type = GlEventTypes.PaymentRunExecuted, PayloadJson = "{}",
        CreatedAt = new DateTime(2026, 5, 1, 12, minute, 0, DateTimeKind.Utc), PublishedAt = published,
    };

    [Fact]
    public async Task Settlement_outbox_survives_store_recreation_and_marks_only_the_tenant_event()
    {
        var collection = _db.GetCollection<global::PaymentService.BankReconciliation.NachaFileSettlement>(
            global::PaymentService.BankReconciliation.NachaSettlementStoreMongo.CollectionName);
        await collection.InsertManyAsync(new[]
        {
            new global::PaymentService.BankReconciliation.NachaFileSettlement { Id = "s-a", TenantId = "t1", FileReference = "same", GlOutbox = { Message("settle-a", 1) } },
            new global::PaymentService.BankReconciliation.NachaFileSettlement { Id = "s-b", TenantId = "t2", FileReference = "same", GlOutbox = { Message("settle-b", 2) } },
        });
        var first = new MongoGlOutboxStore(_db);
        var pending = Assert.Single(await first.ListUnpublishedAsync("t1"));
        Assert.Equal(GlOutboxSource.NachaSettlement, pending.Source);
        var restarted = new MongoGlOutboxStore(_db);
        Assert.Equal("settle-a", Assert.Single(await restarted.ListUnpublishedAsync("t1")).Message.EventId);
        await restarted.MarkPublishedAsync(pending, DateTime.UtcNow);
        Assert.Empty(await first.ListUnpublishedAsync("t1"));
        Assert.Equal("settle-b", Assert.Single(await first.ListUnpublishedAsync("t2")).Message.EventId);
        var saved = await collection.Find(x => x.Id == "s-a").SingleAsync();
        Assert.Equal("same", saved.FileReference);
        Assert.NotNull(Assert.Single(saved.GlOutbox).PublishedAt);
    }

    [Fact]
    public async Task Undelivered_events_are_listed_across_collections_and_marked_in_place()
    {
        await _db.GetCollection<PaymentRun>("PaymentRuns").InsertManyAsync(new[]
        {
            new PaymentRun { Id = "run-1", TenantId = "t1", PaymentRunNumber = "PR-1", GlOutbox = { Message("a", 1), Message("old", 0, DateTime.UtcNow) } },
            new PaymentRun { Id = "run-2", TenantId = "t2", PaymentRunNumber = "PR-2", GlOutbox = { Message("b", 3) } },
            new PaymentRun { Id = "run-3", TenantId = "t1", PaymentRunNumber = "PR-3" },
        });
        await _db.GetCollection<ReversalRun>("ReversalRuns").InsertOneAsync(
            new ReversalRun { Id = "rr-1", TenantId = "t1", ReversalRunNumber = "RR-1", GlOutbox = { Message("c", 2) } });
        await _db.GetCollection<PaymentFileTransmission>("PaymentFileTransmissions").InsertOneAsync(
            new PaymentFileTransmission { Id = "nacha:t1:FFS-1", TenantId = "t1", FileReference = "FFS-1", Outbox = { Message("d", 4) } });
        var store = new MongoGlOutboxStore(_db);
        var now = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        var due = await store.ListDueAsync(now, 10);

        Assert.Equal(new[] { "a", "c", "b", "d" }, due.Select(d => d.Message.EventId));
        Assert.Equal(new[] { GlOutboxSource.PaymentRun, GlOutboxSource.ReversalRun, GlOutboxSource.PaymentRun, GlOutboxSource.PaymentFileTransmission },
            due.Select(d => d.Source));
        Assert.Equal(new[] { "a", "c", "d" }, (await store.ListUnpublishedAsync("t1")).Select(d => d.Message.EventId));

        await store.MarkPublishedAsync(due[0], now);
        await store.MarkFailedAsync(due[1], "ar-service answered 503", now.AddMinutes(5));
        await store.MarkPublishedAsync(due[3], now);

        Assert.Equal(new[] { "b" }, (await store.ListDueAsync(now, 10)).Select(d => d.Message.EventId));
        var reversal = (await store.ListUnpublishedAsync("t1")).Single();
        Assert.Equal(("c", 1, "ar-service answered 503"), (reversal.Message.EventId, reversal.Message.PublishAttempts, reversal.Message.LastError));
        var run1 = await _db.GetCollection<PaymentRun>("PaymentRuns").Find(r => r.Id == "run-1").SingleAsync();
        Assert.Equal("PR-1", run1.PaymentRunNumber); // the run itself is untouched
        Assert.NotNull(run1.GlOutbox.Single(m => m.EventId == "a").PublishedAt);
        Assert.Equal(2, run1.GlOutbox.Count);
    }

    [Fact]
    public async Task A_cycle_reads_a_bounded_number_of_documents_through_an_index_and_skips_backing_off_events()
    {
        var runs = _db.GetCollection<PaymentRun>("PaymentRuns");
        await runs.InsertManyAsync(Enumerable.Range(0, 30).Select(i => new PaymentRun
        {
            Id = $"run-{i:D2}", TenantId = "t1", PaymentRunNumber = $"PR-{i}",
            GlOutbox = { i % 2 == 0 ? Message($"e{i:D2}", i) : Message($"done{i:D2}", i, DateTime.UtcNow) },
        }));
        var backingOff = Message("later", 1);
        backingOff.NextAttemptAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await runs.InsertOneAsync(new PaymentRun { Id = "run-zz", TenantId = "t1", GlOutbox = { backingOff } });
        var store = new MongoGlOutboxStore(_db);
        var now = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        var due = await store.ListDueAsync(now, 5);

        Assert.Equal(5, due.Count);
        Assert.DoesNotContain(due, d => d.Message.EventId == "later" || d.Message.EventId.StartsWith("done"));
        Assert.Equal(16, (await store.ListUnpublishedAsync("t1", 100)).Count);
        Assert.Equal(3, (await store.ListUnpublishedAsync("t1", 3)).Count);
        var indexes = await (await runs.Indexes.ListAsync()).ToListAsync();
        Assert.Contains(indexes, i => i["name"].AsString == "gl_outbox_unpublished_GlOutbox");
    }

    [Fact]
    public async Task A_duplicated_outbox_entry_is_delivered_once_and_marked_with_the_first()
    {
        var runs = _db.GetCollection<PaymentRun>("PaymentRuns");
        await runs.InsertOneAsync(new PaymentRun { Id = "run-dup", TenantId = "t1", GlOutbox = { Message("same", 1), Message("same", 2), Message("other", 3) } });
        var store = new MongoGlOutboxStore(_db);
        var now = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        var due = await store.ListDueAsync(now, 10);
        Assert.Equal(new[] { "same", "other" }, due.Select(d => d.Message.EventId));

        await store.MarkFailedAsync(due[0], "ar-service answered 503", now.AddMinutes(1));
        var stored = await runs.Find(r => r.Id == "run-dup").SingleAsync();
        Assert.All(stored.GlOutbox.Where(m => m.EventId == "same"), m => Assert.Equal(1, m.PublishAttempts));

        await store.MarkPublishedAsync(due[0], now);
        stored = await runs.Find(r => r.Id == "run-dup").SingleAsync();
        Assert.All(stored.GlOutbox.Where(m => m.EventId == "same"), m => Assert.NotNull(m.PublishedAt));
        Assert.Equal(new[] { "other" }, (await store.ListUnpublishedAsync("t1")).Select(d => d.Message.EventId));
    }

    [Fact]
    public async Task A_mark_never_touches_another_tenants_document()
    {
        await _db.GetCollection<PaymentRun>("PaymentRuns").InsertOneAsync(
            new PaymentRun { Id = "run-x", TenantId = "t1", GlOutbox = { Message("a", 1) } });
        var store = new MongoGlOutboxStore(_db);
        var forged = new PendingGlEvent(GlOutboxSource.PaymentRun, "run-x", "t2", Message("a", 1));

        await store.MarkPublishedAsync(forged, DateTime.UtcNow);

        Assert.Single(await store.ListUnpublishedAsync("t1"));
    }
}
