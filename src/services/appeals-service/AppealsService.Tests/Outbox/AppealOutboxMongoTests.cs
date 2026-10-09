using System.Diagnostics.Metrics;
using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Testing.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace AppealsService.Tests.Outbox;

/// <summary>
/// The transactional outbox against a real, standalone mongod (no replica
/// set — the deployment shape the outbox is designed for): every appeal
/// change carries its Kafka event in the same single-document update, and
/// <see cref="AppealOutboxDispatcher"/> relays it with retry, backoff,
/// ordering and dead-lettering.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class AppealOutboxMongoTests : IAsyncLifetime
{
    private const string Tenant = "t-outbox";

    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;
    private readonly InMemoryAppealRepository _audit = new();
    private readonly AppealRepositoryMongo _repo;
    private readonly RecordingAppealEventPublisher _kafka = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    public AppealOutboxMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("appeals_outbox");
        _repo = new AppealRepositoryMongo(_db, _audit);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static AppealOutboxOptions Options(int maxAttempts = 3) => new()
    {
        Enabled = false,
        MaxAttempts = maxAttempts,
        InitialBackoff = TimeSpan.FromSeconds(10),
        MaxBackoff = TimeSpan.FromMinutes(1),
        LeaseDuration = TimeSpan.FromSeconds(30),
        SentRetention = TimeSpan.FromHours(1)
    };

    private AppealOutboxDispatcher Dispatcher(RecordingAppealEventPublisher? kafka = null, AppealOutboxOptions? options = null,
        IAppealOutboxStore? store = null) =>
        new(store ?? _repo, kafka ?? _kafka, options ?? Options(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AppealOutboxDispatcher>.Instance, _time);

    private static AppealEvent Audit(Appeal a, AppealEventType type, AppealStatus? from = null, AppealStatus? to = null) => new()
    {
        TenantId = a.TenantId,
        AppealId = a.Id,
        EventId = Guid.NewGuid().ToString(),
        EventType = type,
        FromStatus = from,
        ToStatus = to,
        ActorId = "user-1",
        OccurredAt = DateTime.UtcNow
    };

    private async Task<Appeal> CreateAsync()
    {
        var appeal = new Appeal
        {
            TenantId = Tenant,
            Id = Guid.NewGuid().ToString(),
            AppealNumber = "APL-" + Guid.NewGuid().ToString("N")[..8],
            ClaimId = "c1",
            ClaimNumber = "CLM-1",
            MemberId = "m1",
            PatientName = "enc::patient",
            ProviderNPI = "1234567890",
            AppealReason = "enc::reason",
            LineOfBusiness = LineOfBusiness.Medicare,
            Status = AppealStatus.Draft
        };
        var genesis = AppealOutbox.Created(Audit(appeal, AppealEventType.AppealCreated, to: AppealStatus.Draft), appeal, "corr");
        return await _repo.CreateAsync(appeal, genesis);
    }

    private async Task<(Appeal Appeal, AppealEvent Event)> TransitionAsync(Appeal appeal, AppealStatus to)
    {
        var from = appeal.Status;
        appeal.Status = to;
        var audit = AppealOutbox.StatusChanged(
            Audit(appeal, AppealEventType.AppealStatusChanged, from, to), appeal, from, to, "corr");
        return (await _repo.TransitionStatusAsync(appeal, audit), audit);
    }

    private async Task<Appeal> StoredAsync(string id) => (await _repo.GetByIdAsync(Tenant, id))!;

    private List<string> ProducedIds(string appealId) =>
        _kafka.Produced.Where(m => m.AppealId == appealId).Select(m => m.EventId).ToList();

    // ── Writing: the event commits with the change ──────────────────────

    [Fact]
    public async Task Every_Change_Writes_Its_Event_Into_The_Appeal_Document()
    {
        var appeal = await CreateAsync();
        var (submitted, transition) = await TransitionAsync(appeal, AppealStatus.Submitted);

        var note = new AppealNote { CreatedBy = "user-1", NoteText = "enc::note" };
        await _repo.AppendNoteAsync(submitted,
            note, AppealOutbox.NoteAdded(Audit(submitted, AppealEventType.AppealNoteAdded), submitted, note, "corr"));

        var stored = await StoredAsync(appeal.Id);
        stored.Outbox!.Select(m => m.EventType).Should().Equal(
            AppealEventPublisher.AppealCreatedType,
            AppealEventPublisher.AppealStatusChangedType,
            AppealEventPublisher.AppealNoteAddedType);
        stored.Outbox.Should().OnlyContain(m => m.Status == AppealOutboxStatus.Pending && m.TenantId == Tenant);
        stored.Outbox[1].EventId.Should().Be(transition.EventId, "the Kafka event id is the audit row's idempotency key");
        stored.Outbox.Should().OnlyContain(m => !m.PayloadJson.Contains("enc::"), "encrypted fields never reach the payload");

        // The raw document holds it under the class-map name: same document, same write.
        var raw = await _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName)
            .Find(new BsonDocument("_id", appeal.Id)).SingleAsync();
        raw["Outbox"].AsBsonArray.Should().HaveCount(3);
        raw["Outbox"][0]["Status"].Should().Be(new BsonString("Pending"));
    }

    [Fact]
    public async Task A_Rejected_Transition_Writes_No_Event()
    {
        var appeal = await CreateAsync();
        var stale = (await StoredAsync(appeal.Id));
        await TransitionAsync(appeal, AppealStatus.Submitted);

        // A second writer still holding the Draft snapshot loses the status filter.
        var act = () => TransitionAsync(stale, AppealStatus.Submitted);
        await act.Should().ThrowAsync<InvalidAppealTransitionException>();
        (await StoredAsync(appeal.Id)).Outbox!.Should().HaveCount(2);
    }

    // ── Relay ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Kafka_Down_At_Publish_Time_Then_The_Event_Is_Published_Later()
    {
        var appeal = await CreateAsync();
        var dispatcher = Dispatcher();

        _kafka.Down = true;
        (await dispatcher.DispatchAppealAsync(Tenant, appeal.Id)).Should().Be(0);

        var pending = (await StoredAsync(appeal.Id)).Outbox!.Single();
        pending.Status.Should().Be(AppealOutboxStatus.Pending);
        pending.Attempts.Should().Be(0, "an outage never uses up the dead-letter budget");
        pending.LastError.Should().Contain("unreachable");

        // Paused with backoff: nothing is attempted until it elapses.
        _kafka.Down = false;
        (await dispatcher.DispatchPendingAsync()).Should().Be(0);
        _time.Advance(TimeSpan.FromSeconds(11));

        (await dispatcher.DispatchPendingAsync()).Should().Be(1);
        ProducedIds(appeal.Id).Should().ContainSingle();
        _kafka.Created.Should().ContainSingle(c => c.AppealId == appeal.Id && c.CorrelationId == "corr");
        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Sent);
        (await _repo.FindPendingAsync(_time.GetUtcNow().UtcDateTime, 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task Crash_Between_The_State_Write_And_The_Publish_Loses_Nothing()
    {
        // The process "crashes" right after the write: no dispatcher ever
        // ran for this appeal in that process.
        var appeal = await CreateAsync();
        var (_, transition) = await TransitionAsync(appeal, AppealStatus.Submitted);

        // A fresh process (new dispatcher instance) finds and publishes both.
        var restarted = Dispatcher();
        (await restarted.DispatchPendingAsync()).Should().Be(2);
        ProducedIds(appeal.Id).Should().HaveCount(2).And.HaveElementAt(1, transition.EventId);
    }

    [Fact]
    public async Task Crash_While_Holding_The_Lease_Is_Taken_Over_After_It_Lapses()
    {
        var appeal = await CreateAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        (await _repo.TryLeaseAsync(Tenant, appeal.Id, "crashed-pod", now, now.AddSeconds(30))).Should().NotBeNull();

        var dispatcher = Dispatcher();
        (await dispatcher.DispatchAppealAsync(Tenant, appeal.Id)).Should().Be(0, "a live lease belongs to another replica");

        _time.Advance(TimeSpan.FromSeconds(31));
        (await dispatcher.DispatchPendingAsync()).Should().Be(1);
        (await StoredAsync(appeal.Id)).OutboxLeaseOwner.Should().BeNull("the lease is released after the run");
    }

    [Fact]
    public async Task Duplicate_Dispatch_After_A_Lost_Ack_Carries_The_Same_Event_Id()
    {
        var appeal = await CreateAsync();

        // Kafka acknowledged, then the process died before marking it sent.
        var store = new FailingMarkStore(_repo, failSentMarks: 1);
        var act = () => Dispatcher(store: store).DispatchAppealAsync(Tenant, appeal.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Pending);

        (await Dispatcher().DispatchPendingAsync()).Should().Be(1);

        var ids = ProducedIds(appeal.Id);
        ids.Should().HaveCount(2, "at-least-once: the event is delivered again");
        ids.Distinct().Should().ContainSingle("both deliveries carry the same idempotency key for consumer de-duplication");
        var payloads = _kafka.Produced.Where(m => m.AppealId == appeal.Id).Select(m => m.PayloadJson).ToList();
        payloads[1].Should().Be(payloads[0], "the redelivery is byte-identical");
        payloads[0].Should().Contain($"\"eventId\":\"{ids[0]}\"");
    }

    [Fact]
    public async Task Events_Of_One_Appeal_Are_Published_In_Write_Order_Even_Across_Failures()
    {
        var appeal = await CreateAsync();
        var (a1, _) = await TransitionAsync(appeal, AppealStatus.Submitted);
        var (a2, _) = await TransitionAsync(a1, AppealStatus.InReview);
        await TransitionAsync(a2, AppealStatus.PendingInfo);
        var written = (await StoredAsync(appeal.Id)).Outbox!.Select(m => m.EventId).ToList();

        // The first event is rejected once: nothing after it may overtake it.
        var dispatcher = Dispatcher();
        _kafka.FailNext(new InvalidOperationException("rejected once"));
        await dispatcher.DispatchAppealAsync(Tenant, appeal.Id); // the first event fails
        ProducedIds(appeal.Id).Should().BeEmpty();

        _time.Advance(TimeSpan.FromSeconds(5));
        (await dispatcher.DispatchPendingAsync()).Should().Be(0, "the first event is backing off and the other three wait behind it");

        _time.Advance(TimeSpan.FromSeconds(6));
        (await dispatcher.DispatchPendingAsync()).Should().Be(4);
        ProducedIds(appeal.Id).Should().Equal(written);
        (await StoredAsync(appeal.Id)).Outbox!.First().Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Ordering_Holds_Across_Appeals_Independently()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        await TransitionAsync(second, AppealStatus.Submitted);

        // Appeal "first" is blocked; appeal "second" is not held up by it.
        _kafka.FailNext(new InvalidOperationException("rejected"));
        await Dispatcher().DispatchAppealAsync(Tenant, first.Id);
        await Dispatcher().DispatchAppealAsync(Tenant, second.Id);

        ProducedIds(first.Id).Should().BeEmpty();
        ProducedIds(second.Id).Should().HaveCount(2);
    }

    [Fact]
    public async Task Poison_Event_Is_Dead_Lettered_After_Max_Attempts_With_A_Metric_And_Replay_Delivers_It()
    {
        var appeal = await CreateAsync();
        var (_, transition) = await TransitionAsync(appeal, AppealStatus.Submitted);
        var dispatcher = Dispatcher(options: Options(maxAttempts: 3));

        var deadLettered = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "cho.appeals.outbox.outcomes.total") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "cho.outcome" && (string?)tag.Value == "dead_lettered") Interlocked.Add(ref deadLettered, value);
        });
        listener.Start();

        _kafka.FailNext(new Confluent.Kafka.KafkaException(Confluent.Kafka.ErrorCode.MsgSizeTooLarge), times: 3);
        for (var i = 0; i < 3; i++)
        {
            await dispatcher.DispatchPendingAsync();
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        var outbox = (await StoredAsync(appeal.Id)).Outbox!;
        var poison = outbox[0];
        poison.Status.Should().Be(AppealOutboxStatus.DeadLettered);
        poison.Attempts.Should().Be(3);
        poison.CompletedAt.Should().NotBeNull();
        poison.LastError.Should().Contain("KafkaException");
        Interlocked.Read(ref deadLettered).Should().Be(1);

        // The appeal's later event is not stuck behind the dead letter.
        outbox[1].Status.Should().Be(AppealOutboxStatus.Sent);
        ProducedIds(appeal.Id).Should().Equal(transition.EventId);

        // Dead letters are never retried or pruned on their own...
        _time.Advance(TimeSpan.FromDays(3));
        (await dispatcher.DispatchPendingAsync()).Should().Be(0);
        (await StoredAsync(appeal.Id)).Outbox!.Should().ContainSingle(m => m.Status == AppealOutboxStatus.DeadLettered);

        // ...until an operator replays them.
        (await _repo.RequeueDeadLetteredAsync(Tenant, appeal.Id, eventId: null)).Should().Be(1);
        (await dispatcher.DispatchPendingAsync()).Should().Be(1);
        ProducedIds(appeal.Id).Should().Equal(transition.EventId, poison.EventId);
        (await StoredAsync(appeal.Id)).Outbox!.Should().ContainSingle("the entry sent days ago was pruned")
            .Which.Should().Match<AppealOutboxMessage>(m => m.EventId == poison.EventId && m.Status == AppealOutboxStatus.Sent);
    }

    [Fact]
    public async Task Kafka_Disabled_Keeps_Events_Pending_Until_Kafka_Is_Configured()
    {
        var appeal = await CreateAsync();
        var disabled = new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled);

        (await Dispatcher(disabled).DispatchPendingAsync()).Should().Be(0);
        disabled.Attempts.Should().Be(0);
        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Pending);

        // Kafka configured on a later start: the backlog is delivered.
        (await Dispatcher().DispatchPendingAsync()).Should().Be(1);
        ProducedIds(appeal.Id).Should().ContainSingle();
    }

    [Fact]
    public async Task Kafka_Disabled_With_Skip_Marks_Events_Skipped_And_Prunes_Them()
    {
        var appeal = await CreateAsync();
        var disabled = new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled);
        var options = Options();
        options.SkipWhenKafkaDisabled = true;

        await Dispatcher(disabled, options).DispatchPendingAsync();

        disabled.Attempts.Should().Be(0);
        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Skipped);
        (await _repo.FindPendingAsync(_time.GetUtcNow().UtcDateTime, 10)).Should().BeEmpty();

        _time.Advance(TimeSpan.FromHours(2));
        await TransitionAsync(await StoredAsync(appeal.Id), AppealStatus.Submitted);
        await Dispatcher(disabled, options).DispatchPendingAsync();
        (await StoredAsync(appeal.Id)).Outbox!.Should().ContainSingle()
            .Which.EventType.Should().Be(AppealEventPublisher.AppealStatusChangedType, "the old skipped entry was pruned");
    }

    [Fact]
    public async Task Relay_Updates_Never_Overwrite_Concurrent_Appeal_Changes()
    {
        var appeal = await CreateAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        var leased = await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", now, now.AddSeconds(30));

        // An appeal change lands while the relay holds the lease.
        await TransitionAsync(appeal, AppealStatus.Submitted);

        var entry = leased!.Single();
        entry.Status = AppealOutboxStatus.Sent;
        entry.CompletedAt = now;
        await _repo.UpdateMessageAsync(Tenant, appeal.Id, entry);
        await _repo.ReleaseLeaseAsync(Tenant, appeal.Id, "pod-a", now.AddHours(-1));

        var stored = await StoredAsync(appeal.Id);
        stored.Status.Should().Be(AppealStatus.Submitted);
        stored.Outbox!.Select(m => m.Status).Should().Equal(AppealOutboxStatus.Sent, AppealOutboxStatus.Pending);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now;
        public ManualTime(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Fails the first N "mark sent" writes, as a crash right after the broker ack would.</summary>
    private sealed class FailingMarkStore : IAppealOutboxStore
    {
        private readonly IAppealOutboxStore _inner;
        private int _failSentMarks;

        public FailingMarkStore(IAppealOutboxStore inner, int failSentMarks)
        {
            _inner = inner;
            _failSentMarks = failSentMarks;
        }

        public Task<IReadOnlyList<AppealOutboxKey>> FindPendingAsync(DateTime now, int limit, CancellationToken ct = default) =>
            _inner.FindPendingAsync(now, limit, ct);

        public Task<IReadOnlyList<AppealOutboxMessage>?> TryLeaseAsync(string tenantId, string appealId, string owner,
            DateTime now, DateTime leaseUntil, CancellationToken ct = default) =>
            _inner.TryLeaseAsync(tenantId, appealId, owner, now, leaseUntil, ct);

        public Task ReleaseLeaseAsync(string tenantId, string appealId, string owner, DateTime pruneCompletedBefore,
            CancellationToken ct = default) =>
            _inner.ReleaseLeaseAsync(tenantId, appealId, owner, pruneCompletedBefore, ct);

        public Task UpdateMessageAsync(string tenantId, string appealId, AppealOutboxMessage message, CancellationToken ct = default)
        {
            if (message.Status == AppealOutboxStatus.Sent && Interlocked.Decrement(ref _failSentMarks) >= 0)
                throw new InvalidOperationException("crash after broker ack (test)");
            return _inner.UpdateMessageAsync(tenantId, appealId, message, ct);
        }

        public Task<int> RequeueDeadLetteredAsync(string tenantId, string appealId, string? eventId, CancellationToken ct = default) =>
            _inner.RequeueDeadLetteredAsync(tenantId, appealId, eventId, ct);
    }
}
