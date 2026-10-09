using System.Diagnostics.Metrics;
using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Tests.Fakes;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace AppealsService.Tests.Outbox;

/// <summary>
/// The transactional outbox against a real, standalone mongod (no replica
/// set — the deployment shape the outbox is designed for): every appeal
/// change carries its Kafka event in the same single-document update, and
/// <see cref="AppealOutboxDispatcher"/> relays it with retry, backoff,
/// ordering, sequencing, lease fencing, dead-lettering and housekeeping.
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
    private readonly ManualTime _time = new(DateTimeOffset.UtcNow);

    public AppealOutboxMongoTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("appeals_outbox");
        _repo = new AppealRepositoryMongo(_db, _audit);
    }

    public Task InitializeAsync() =>
        new AppealIndexInitializer(_db, NullLogger<AppealIndexInitializer>.Instance).StartAsync(CancellationToken.None);

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static AppealOutboxOptions Options(int maxAttempts = 3) => new()
    {
        Enabled = false,
        AwaitInlineDispatch = true,
        MaxAttempts = maxAttempts,
        InitialBackoff = TimeSpan.FromSeconds(10),
        MaxBackoff = TimeSpan.FromMinutes(1),
        LeaseDuration = TimeSpan.FromSeconds(30),
        SentRetention = TimeSpan.FromHours(1)
    };

    private AppealOutboxDispatcher Dispatcher(RecordingAppealEventPublisher? kafka = null, AppealOutboxOptions? options = null,
        string? instance = null) =>
        new(_repo, kafka ?? _kafka, options ?? Options(), NullLogger<AppealOutboxDispatcher>.Instance, _time)
        {
            InstanceId = instance ?? "pod-" + Guid.NewGuid().ToString("N")[..6]
        };

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static AppealEvent Audit(Appeal a, AppealEventType type, AppealStatus? from = null, AppealStatus? to = null,
        string? eventId = null) => new()
    {
        TenantId = a.TenantId,
        AppealId = a.Id,
        EventId = eventId ?? Guid.NewGuid().ToString(),
        EventType = type,
        FromStatus = from,
        ToStatus = to,
        ActorId = "user-1",
        OccurredAt = DateTime.UtcNow
    };

    private async Task<Appeal> CreateAsync(string tenant = Tenant)
    {
        var appeal = new Appeal
        {
            TenantId = tenant,
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

    private Task<Appeal> AddNoteAsync(Appeal appeal, string? eventId = null)
    {
        var note = new AppealNote { CreatedBy = "user-1", NoteText = "enc::note" };
        return _repo.AppendNoteAsync(appeal, note,
            AppealOutbox.NoteAdded(Audit(appeal, AppealEventType.AppealNoteAdded, eventId: eventId), appeal, note, "corr"));
    }

    private async Task<Appeal> StoredAsync(string id, string tenant = Tenant) => (await _repo.GetByIdAsync(tenant, id))!;

    private List<AppealOutboxMessage> Produced(string appealId) =>
        _kafka.Produced.Where(m => m.AppealId == appealId).ToList();

    private List<string> ProducedIds(string appealId) => Produced(appealId).Select(m => m.EventId).ToList();

    private async Task SweepAsync(AppealOutboxDispatcher dispatcher, int times = 1)
    {
        for (var i = 0; i < times; i++) await dispatcher.DispatchPendingAsync();
    }

    // ── Writing: the event commits with the change ──────────────────────

    [Fact]
    public async Task Every_Change_Writes_Its_Event_Into_The_Appeal_Document()
    {
        var appeal = await CreateAsync();
        var (submitted, transition) = await TransitionAsync(appeal, AppealStatus.Submitted);
        await AddNoteAsync(submitted);

        var stored = await StoredAsync(appeal.Id);
        stored.Outbox!.Select(m => m.EventType).Should().Equal(
            AppealEventPublisher.AppealCreatedType,
            AppealEventPublisher.AppealStatusChangedType,
            AppealEventPublisher.AppealNoteAddedType);
        stored.Outbox.Should().OnlyContain(m => m.Status == AppealOutboxStatus.Pending && m.TenantId == Tenant);
        stored.Outbox.Select(m => m.Id).Should().OnlyHaveUniqueItems("every row has its own server id");
        stored.Outbox[1].IdempotencyKey.Should().Be(transition.EventId);
        stored.Outbox[1].EventId.Should().Be(AppealOutbox.WireEventId(Tenant, appeal.Id, transition.EventId));
        stored.Outbox.Should().OnlyContain(m => !m.PayloadJson.Contains("enc::"), "encrypted fields never reach the payload");

        var raw = await _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName)
            .Find(new BsonDocument("_id", appeal.Id)).SingleAsync();
        raw["Outbox"].AsBsonArray.Should().HaveCount(3);
        raw["Outbox"][0]["Status"].Should().Be(new BsonString("Pending"));
        raw.Contains("OutboxNextDueAt").Should().BeFalse("the Cosmos sweep fields are not stored in Mongo");
    }

    [Fact]
    public async Task A_Change_Without_An_Outbox_Message_Is_Refused()
    {
        var appeal = await CreateAsync();
        var bare = Audit(appeal, AppealEventType.AppealNoteAdded);

        var act = () => _repo.AppendNoteAsync(appeal, new AppealNote { NoteText = "enc::x" }, bare);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*carries no outbox message*");
        (await StoredAsync(appeal.Id)).Notes.Should().BeEmpty("nothing was written");
    }

    [Fact]
    public async Task A_Rejected_Transition_Writes_No_Event()
    {
        var appeal = await CreateAsync();
        var stale = await StoredAsync(appeal.Id);
        await TransitionAsync(appeal, AppealStatus.Submitted);

        var act = () => TransitionAsync(stale, AppealStatus.Submitted);
        await act.Should().ThrowAsync<InvalidAppealTransitionException>();
        (await StoredAsync(appeal.Id)).Outbox!.Should().HaveCount(2);
    }

    // ── BLOCKER fix: duplicate EventId ──────────────────────────────────

    [Fact]
    public async Task Retry_With_The_Same_EventId_Appends_Nothing_And_Publishes_Once()
    {
        var appeal = await CreateAsync();
        var key = Guid.NewGuid().ToString();

        await AddNoteAsync(appeal, key);
        var replay = await AddNoteAsync(appeal, key); // client retry

        replay.Notes.Should().ContainSingle("the retry is a replay, not a second note");
        replay.Outbox!.Count(m => m.IdempotencyKey == key).Should().Be(1);
        (await _audit.ListByAppealAsync(Tenant, appeal.Id)).Count(e => e.EventId == key).Should().Be(1);

        await SweepAsync(Dispatcher(), times: 5);
        Produced(appeal.Id).Should().HaveCount(2, "genesis + one note, however many sweeps run");
        (await StoredAsync(appeal.Id)).Outbox!.Should().OnlyContain(m => m.Status == AppealOutboxStatus.Sent);
    }

    [Fact]
    public async Task Rows_Sharing_An_EventId_Are_Tracked_By_Their_Own_Id_And_Never_Republished_Forever()
    {
        // Rows written before the idempotency guard could share an event id.
        var appeal = await CreateAsync();
        var first = (await StoredAsync(appeal.Id)).Outbox!.Single();
        var twin = new BsonDocument
        {
            { "_id", Guid.NewGuid().ToString("N") }, { "IdempotencyKey", first.IdempotencyKey }, // class map: Id -> _id
            { "EventId", first.EventId }, { "EventType", first.EventType }, { "TenantId", Tenant },
            { "AppealId", appeal.Id }, { "PayloadJson", first.PayloadJson }, { "CreatedAt", DateTime.UtcNow },
            { "Status", "Pending" }, { "Attempts", 0 }
        };
        await _db.GetCollection<BsonDocument>(AppealRepositoryMongo.AppealsCollectionName)
            .UpdateOneAsync(new BsonDocument("_id", appeal.Id), new BsonDocument("$push", new BsonDocument("Outbox", twin)));

        await SweepAsync(Dispatcher(), times: 5);

        Produced(appeal.Id).Should().HaveCount(2, "each row is published once and then marked sent by its own id");
        (await StoredAsync(appeal.Id)).Outbox!.Should().OnlyContain(m => m.Status == AppealOutboxStatus.Sent);
    }

    // ── MAJOR: sweep uses the partial index ─────────────────────────────

    [Fact]
    public async Task Sweep_Query_Uses_The_Pending_Partial_Index()
    {
        for (var i = 0; i < 5; i++) await CreateAsync();

        var collection = _db.GetCollection<Appeal>(AppealRepositoryMongo.AppealsCollectionName);
        var rendered = AppealRepositoryMongo.DueFilter(Now).Render(
            new RenderArgs<Appeal>(collection.DocumentSerializer, BsonSerializer.SerializerRegistry));
        var explain = await _db.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "explain", new BsonDocument { { "find", AppealRepositoryMongo.AppealsCollectionName }, { "filter", rendered } } },
            { "verbosity", "queryPlanner" }
        });

        var plan = explain["queryPlanner"]["winningPlan"].ToJson();
        plan.Should().Contain("IXSCAN").And.Contain("ix_outbox_pending");
        plan.Should().NotContain("COLLSCAN");
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

        // Paused: neither the sweep nor an inline nudge even takes the lease.
        _kafka.Down = false;
        var attempts = _kafka.Attempts;
        (await dispatcher.DispatchPendingAsync()).Should().Be(0);
        await dispatcher.NotifyChangedAsync(Tenant, appeal.Id);
        _kafka.Attempts.Should().Be(attempts);
        (await StoredAsync(appeal.Id)).OutboxLeaseOwner.Should().BeNull();

        _time.Advance(TimeSpan.FromSeconds(11));
        (await dispatcher.DispatchPendingAsync()).Should().Be(1);
        _kafka.Created.Should().ContainSingle(c => c.AppealId == appeal.Id && c.CorrelationId == "corr");
        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Sent);
        (await _repo.FindDueAsync(Now, 10)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(Confluent.Kafka.ErrorCode.UnknownTopicOrPart)]
    [InlineData(Confluent.Kafka.ErrorCode.Local_UnknownTopic)]
    [InlineData(Confluent.Kafka.ErrorCode.TopicAuthorizationFailed)]
    [InlineData(Confluent.Kafka.ErrorCode.ClusterAuthorizationFailed)]
    [InlineData(Confluent.Kafka.ErrorCode.SaslAuthenticationFailed)]
    [InlineData(Confluent.Kafka.ErrorCode.Local_Authentication)]
    public async Task Broker_Wide_Errors_Pause_The_Relay_And_Never_Dead_Letter(Confluent.Kafka.ErrorCode code)
    {
        var appeal = await CreateAsync();
        var dispatcher = Dispatcher(options: Options(maxAttempts: 2));

        for (var i = 0; i < 20; i++)
        {
            _kafka.FailNext(new Confluent.Kafka.KafkaException(code));
            await dispatcher.DispatchPendingAsync();
            _time.Advance(TimeSpan.FromMinutes(2));
        }

        var entry = (await StoredAsync(appeal.Id)).Outbox!.Single();
        entry.Status.Should().Be(AppealOutboxStatus.Pending);
        entry.Attempts.Should().Be(0);

        await dispatcher.DispatchPendingAsync();
        Produced(appeal.Id).Should().ContainSingle("published once the broker-side cause is fixed");
    }

    [Fact]
    public async Task Crash_Between_The_State_Write_And_The_Publish_Loses_Nothing()
    {
        var appeal = await CreateAsync();
        var (_, transition) = await TransitionAsync(appeal, AppealStatus.Submitted);

        // A fresh process (new dispatcher instance) finds and publishes both.
        (await Dispatcher().DispatchPendingAsync()).Should().Be(2);
        ProducedIds(appeal.Id).Should().HaveCount(2)
            .And.HaveElementAt(1, AppealOutbox.WireEventId(Tenant, appeal.Id, transition.EventId));
    }

    [Fact]
    public async Task Crash_While_Holding_The_Lease_Is_Taken_Over_After_It_Lapses()
    {
        var appeal = await CreateAsync();
        (await _repo.TryLeaseAsync(Tenant, appeal.Id, "crashed-pod", Now, Now.AddSeconds(30))).Should().NotBeNull();

        var dispatcher = Dispatcher();
        (await dispatcher.DispatchAppealAsync(Tenant, appeal.Id)).Should().Be(0, "a live lease belongs to another replica");

        _time.Advance(TimeSpan.FromSeconds(31));
        (await dispatcher.DispatchPendingAsync()).Should().Be(1);
        (await StoredAsync(appeal.Id)).OutboxLeaseOwner.Should().BeNull("the lease is released after the run");
    }

    [Fact]
    public async Task A_Stale_Lease_Holder_Cannot_Overwrite_The_New_Holders_Result()
    {
        var appeal = await CreateAsync();
        var stale = (await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)))!;

        // pod-a stalls past its lease; pod-b takes over and publishes.
        _time.Advance(TimeSpan.FromSeconds(31));
        (await Dispatcher(instance: "pod-b").DispatchPendingAsync()).Should().Be(1);

        // pod-a wakes up and tries to record a failure / renew: refused.
        var entry = stale.Entries.Single();
        entry.Attempts = 5;
        entry.LastError = "stale";
        (await _repo.UpdateMessageAsync(Tenant, appeal.Id, "pod-a", entry)).Should().BeFalse();
        (await _repo.RenewLeaseAsync(Tenant, appeal.Id, "pod-a", Now.AddSeconds(30), null)).Should().BeFalse();

        var stored = (await StoredAsync(appeal.Id)).Outbox!.Single();
        stored.Status.Should().Be(AppealOutboxStatus.Sent);
        stored.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Duplicate_Dispatch_After_A_Lost_Ack_Carries_The_Same_Event_Id_And_Sequence()
    {
        var appeal = await CreateAsync();

        // Kafka acknowledged, then the pod died before marking it sent
        // (its lease simply lapses).
        var lease = (await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30)))!;
        var entry = lease.Entries.Single();
        (await _repo.RenewLeaseAsync(Tenant, appeal.Id, "pod-a", Now.AddSeconds(30),
            new AppealOutboxSequenceAssignment(entry.Id, 1))).Should().BeTrue();
        entry.Sequence = 1;
        await _kafka.ProduceAsync(entry, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(31));
        (await Dispatcher().DispatchPendingAsync()).Should().Be(1);

        var produced = Produced(appeal.Id);
        produced.Should().HaveCount(2, "at-least-once: the event is delivered again");
        produced.Select(m => m.EventId).Distinct().Should().ContainSingle("consumers de-duplicate on it");
        produced[1].PayloadJson.Should().Be(produced[0].PayloadJson, "the redelivery is byte-identical");
        produced[0].PayloadJson.Should().Contain($"\"eventId\":\"{produced[0].EventId}\"").And.Contain("\"sequence\":1");
    }

    [Fact]
    public async Task Events_Of_One_Appeal_Are_Published_In_Write_Order_With_Increasing_Sequence()
    {
        var appeal = await CreateAsync();
        var (a1, _) = await TransitionAsync(appeal, AppealStatus.Submitted);
        var (a2, _) = await TransitionAsync(a1, AppealStatus.InReview);
        await TransitionAsync(a2, AppealStatus.PendingInfo);
        var written = (await StoredAsync(appeal.Id)).Outbox!.Select(m => m.EventId).ToList();

        // The first event is rejected once: nothing after it may overtake it.
        var dispatcher = Dispatcher();
        _kafka.FailNext(new InvalidOperationException("rejected once"));
        await dispatcher.DispatchAppealAsync(Tenant, appeal.Id);
        ProducedIds(appeal.Id).Should().BeEmpty();

        _time.Advance(TimeSpan.FromSeconds(5));
        (await dispatcher.DispatchPendingAsync()).Should().Be(0, "the first event is backing off and the rest wait behind it");

        _time.Advance(TimeSpan.FromSeconds(6));
        (await dispatcher.DispatchPendingAsync()).Should().Be(4);
        ProducedIds(appeal.Id).Should().Equal(written);
        Produced(appeal.Id).Select(m => m.Sequence).Should().Equal(1L, 2L, 3L, 4L);
        (await StoredAsync(appeal.Id)).OutboxSequence.Should().Be(4);
    }

    [Fact]
    public async Task Ordering_Holds_Across_Appeals_Independently()
    {
        var first = await CreateAsync();
        var second = await CreateAsync();
        await TransitionAsync(second, AppealStatus.Submitted);

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
        var outcomes = ListenToOutcomes();

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
        poison.ExpiresAt.Should().NotBeNull();
        poison.LastError.Should().Contain("KafkaException");
        outcomes.Count("dead_lettered").Should().Be(1);

        outbox[1].Status.Should().Be(AppealOutboxStatus.Sent, "later events are not stuck behind the dead letter");
        ProducedIds(appeal.Id).Should().Equal(AppealOutbox.WireEventId(Tenant, appeal.Id, transition.EventId));

        (await _repo.RequeueDeadLetteredAsync(Tenant, appeal.Id, eventId: poison.EventId)).Should().Be(1);
        (await dispatcher.DispatchPendingAsync()).Should().Be(1);
        ProducedIds(appeal.Id).Should().Equal(outbox[1].EventId, poison.EventId);
        Produced(appeal.Id).Select(m => m.Sequence).Should().Equal(new long?[] { 2, 1 },
            "a replayed event keeps the sequence it was first given, so last-write-wins consumers ignore it");
        outcomes.Dispose();
    }

    [Fact]
    public async Task Bulk_Replay_Requeues_Dead_Letters_Tenant_Wide_Or_Platform_Wide()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        var other = await CreateAsync("t-other");
        var dispatcher = Dispatcher(options: Options(maxAttempts: 1));
        _kafka.FailNext(new InvalidOperationException("rejected"), times: 3);
        await SweepAsync(dispatcher);
        foreach (var x in new[] { a, b }) (await StoredAsync(x.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.DeadLettered);
        (await StoredAsync(other.Id, "t-other")).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.DeadLettered);

        (await _repo.RequeueAllDeadLetteredAsync(Tenant)).Should().Be(2);
        (await StoredAsync(other.Id, "t-other")).Outbox!.Single().Status
            .Should().Be(AppealOutboxStatus.DeadLettered, "another tenant is untouched");
        (await _repo.RequeueAllDeadLetteredAsync(tenantId: null)).Should().Be(1);

        await SweepAsync(dispatcher);
        _kafka.Produced.Should().HaveCount(3);
    }

    // ── MAJOR: bounded growth, gauges ───────────────────────────────────

    [Fact]
    public async Task Stale_And_Overflowing_Pending_Events_Are_Dead_Lettered_And_Old_Dead_Letters_Pruned()
    {
        var appeal = await CreateAsync();
        var current = appeal;
        for (var i = 0; i < 4; i++) current = await AddNoteAsync(current);
        var disabled = new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled);
        var options = Options();
        options.MaxPendingPerAppeal = 3;
        options.DeadLetterRetention = TimeSpan.FromDays(30);
        var outcomes = ListenToOutcomes();

        // Kafka disabled (retain): housekeeping still bounds the outbox.
        await SweepAsync(Dispatcher(disabled, options));
        var outbox = (await StoredAsync(appeal.Id)).Outbox!;
        outbox.Take(2).Should().OnlyContain(m => m.Status == AppealOutboxStatus.DeadLettered && m.LastError!.StartsWith("Overflow"));
        outbox.Skip(2).Should().OnlyContain(m => m.Status == AppealOutboxStatus.Pending);
        outcomes.Count("overflow").Should().Be(2);

        _time.Advance(TimeSpan.FromDays(8));
        await SweepAsync(Dispatcher(disabled, options));
        (await StoredAsync(appeal.Id)).Outbox!.Should().OnlyContain(m => m.Status == AppealOutboxStatus.DeadLettered);
        outcomes.Count("expired").Should().Be(3);

        _time.Advance(TimeSpan.FromDays(31));
        await SweepAsync(Dispatcher(disabled, options));
        (await StoredAsync(appeal.Id)).Outbox!.Should().BeEmpty("dead letters past retention are pruned");
        outcomes.Count("dead_letter_pruned").Should().Be(5);
        outcomes.Dispose();
    }

    [Fact]
    public async Task Completed_Entries_Are_Capped_Per_Appeal()
    {
        var appeal = await CreateAsync();
        var current = appeal;
        for (var i = 0; i < 6; i++) current = await AddNoteAsync(current);
        var options = Options();
        options.MaxCompletedPerAppeal = 2;

        await SweepAsync(Dispatcher(options: options));

        Produced(appeal.Id).Should().HaveCount(7);
        (await StoredAsync(appeal.Id)).Outbox!.Should().HaveCount(2).And.OnlyContain(m => m.Status == AppealOutboxStatus.Sent);
    }

    [Fact]
    public async Task Backlog_Stats_Report_Pending_Count_And_Oldest_Age()
    {
        var appeal = await CreateAsync();
        await TransitionAsync(appeal, AppealStatus.Submitted);

        var stats = await _repo.GetStatsAsync();
        stats.Pending.Should().BeGreaterThanOrEqualTo(2);
        stats.OldestPendingCreatedAt.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        await SweepAsync(Dispatcher());
        (await _repo.GetStatsAsync()).Pending.Should().Be(0);
    }

    // ── Kafka-disabled modes ────────────────────────────────────────────

    [Fact]
    public async Task Kafka_Disabled_Keeps_Events_Pending_Until_Kafka_Is_Configured()
    {
        var appeal = await CreateAsync();
        var disabled = new RecordingAppealEventPublisher(AppealEventPublisherState.Disabled);

        (await Dispatcher(disabled).DispatchPendingAsync()).Should().Be(0);
        disabled.Attempts.Should().Be(0);
        (await StoredAsync(appeal.Id)).Outbox!.Single().Status.Should().Be(AppealOutboxStatus.Pending);

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
        (await _repo.FindDueAsync(Now, 10)).Should().BeEmpty();

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
        var lease = await _repo.TryLeaseAsync(Tenant, appeal.Id, "pod-a", Now, Now.AddSeconds(30));

        await TransitionAsync(appeal, AppealStatus.Submitted);

        var entry = lease!.Entries.Single();
        entry.Status = AppealOutboxStatus.Sent;
        entry.CompletedAt = Now;
        (await _repo.UpdateMessageAsync(Tenant, appeal.Id, "pod-a", entry)).Should().BeTrue();
        await _repo.ReleaseLeaseAsync(Tenant, appeal.Id, "pod-a", Array.Empty<string>());

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

    private static OutcomeListener ListenToOutcomes() => new();

    /// <summary>
    /// Counts cho.appeals.outbox.outcomes.total by outcome. Filters on event
    /// ids is not possible on the metric (no identity labels), so counts are
    /// only meaningful within this Mongo collection's sequential tests.
    /// </summary>
    private sealed class OutcomeListener : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = new();

        public OutcomeListener()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "cho.appeals.outbox.outcomes.total") l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "cho.outcome" && tag.Value is string outcome)
                        lock (_counts) _counts[outcome] = _counts.GetValueOrDefault(outcome) + value;
            });
            _listener.Start();
        }

        public long Count(string outcome)
        {
            lock (_counts) return _counts.GetValueOrDefault(outcome);
        }

        public void Dispose() => _listener.Dispose();
    }
}
