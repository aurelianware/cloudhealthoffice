using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.Events.Kafka;
using CloudHealthOffice.Testing.Mongo;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// The claims.finalized.v1 outbox over real MongoDB (EphemeralMongo): the
/// event is stored on the claim in the same write as the Paid / Voided status
/// and removed only once Kafka acknowledged it. Before, the publisher logged
/// and dropped a failed produce right after the write, so accumulator-service
/// could silently miss a finalized claim.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimEventOutboxTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private IMongoDatabase _db = null!;
    private ClaimRepositoryMongo _repo = null!;
    private readonly FakeProducer _producer = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 5, 4, 12, 0, 0, TimeSpan.Zero));
    private ClaimEventPublisher _publisher = null!;
    private ClaimEventOutboxDispatcher _dispatcher = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("claim_event_outbox");
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        _repo = new ClaimRepositoryMongo(_db, new HttpContextAccessor { HttpContext = ctx }, NullLogger<ClaimRepositoryMongo>.Instance);
        var scopes = new ServiceCollection().AddSingleton<IClaimRepository>(_repo).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new ClaimEventOutboxOptions());
        var drainer = new ClaimEventOutboxDrainer(_producer, options, NullLogger<ClaimEventOutboxDrainer>.Instance, _clock);
        _publisher = new ClaimEventPublisher(_producer, drainer, scopes, NullLogger<ClaimEventPublisher>.Instance, _clock);
        _dispatcher = new ClaimEventOutboxDispatcher(scopes, drainer, options, NullLogger<ClaimEventOutboxDispatcher>.Instance, _clock);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private ClaimFinalizationService Finalization() => new(
        _repo, Substitute.For<IClaimVersionEventPublisher>(), _publisher,
        Substitute.For<IClaimAdjustmentService>(), NullLogger<ClaimFinalizationService>.Instance);

    private async Task<Claim> SeedApprovedAsync(string id) => await _repo.CreateAsync(new Claim
    {
        Id = id, ClaimVersionId = id, TenantId = Tenant, ClaimNumber = "CN-" + id, VersionNumber = 1,
        Status = ClaimStatus.Approved, VersionState = ClaimVersionState.Adjudicated,
        MemberId = "M1", BillingProviderNPI = "1234567890", LineOfBusiness = LineOfBusiness.Commercial,
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 1000m,
        AdjudicationResult = new AdjudicationResult { PayerPayment = 800m, AllowedAmount = 1000m, DeductibleAmount = 200m },
    });

    private static ClaimFinalizationRequest Paid() => new()
    {
        CheckNumber = "CHK-1", PaymentDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc), PayerPayment = 800m,
    };

    private async Task<List<ClaimEventOutboxItem>> PendingAsync(string id) =>
        (await _repo.GetForAccumulatorOutboxAsync(Tenant, id))!.PendingClaimEvents ?? [];

    private static ClaimFinalizedEvent Event(FakeProducer.Message m) =>
        JsonSerializer.Deserialize<ClaimFinalizedEvent>(m.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task Finalize_StoresTheEventWithThePaidStatus_PublishesIt_AndClearsIt()
    {
        await SeedApprovedAsync("C1");

        await Finalization().FinalizeAsync("C1", Paid(), Tenant, "actor", "corr");

        var message = _producer.Published.Should().ContainSingle().Subject;
        message.Topic.Should().Be(ClaimEventPublisher.ClaimFinalizedTopic);
        message.Key.Should().Be("C1");
        var evt = Event(message);
        evt.FinalStatus.Should().Be("Paid");
        evt.DeductibleApplied.Should().Be(200m);
        message.Headers["event-id"].Should().Be(evt.EventId);
        (await PendingAsync("C1")).Should().BeEmpty();
    }

    [Fact]
    public async Task ACrashBetweenTheWriteAndThePublish_LosesNothing()
    {
        // The finalize write with its event, then the process dies before publishing.
        var claim = await SeedApprovedAsync("C1");
        claim.Status = ClaimStatus.Paid;
        claim.VersionState = ClaimVersionState.Paid;
        claim.AddPendingClaimEvent(_publisher.PrepareClaimFinalized(claim, Tenant));
        await _repo.UpdateAsync(claim);
        _producer.Published.Should().BeEmpty();

        // The dispatcher leaves it to the writer for the inline grace, then publishes it.
        (await _dispatcher.RunOnceAsync(CancellationToken.None)).Should().Be(0);
        _clock.Advance(ClaimEventOutboxItem.InlineGrace + TimeSpan.FromSeconds(1));
        (await _dispatcher.RunOnceAsync(CancellationToken.None)).Should().Be(1);

        Event(_producer.Published.Single()).FinalStatus.Should().Be("Paid");
        (await PendingAsync("C1")).Should().BeEmpty();
        (await _dispatcher.RunOnceAsync(CancellationToken.None)).Should().Be(0, "it is published once, then gone");
    }

    [Fact]
    public async Task AKafkaOutage_KeepsTheEvents_AndTheyDrainInOrderAfterwards()
    {
        await SeedApprovedAsync("C1");
        _producer.Down = true;

        await Finalization().FinalizeAsync("C1", Paid(), Tenant, "actor", "corr");
        await Finalization().VoidAsync("C1", new ClaimVoidRequest { Reason = "duplicate payment" }, Tenant, "actor", "corr");

        var pending = await PendingAsync("C1");
        pending.Should().HaveCount(2);
        var paid = pending.OrderBy(e => e.CreatedAtMs).First();
        paid.Attempts.Should().BeGreaterThan(0);
        paid.LastError.Should().Contain("ProduceException");
        // The Reversed event waited behind the unacknowledged Paid one: never out of order.
        _producer.Attempted.Select(a => Event(a).FinalStatus).Should().OnlyContain(s => s == "Paid");

        _producer.Down = false;
        _clock.Advance(TimeSpan.FromMinutes(11)); // past the longest backoff
        (await _dispatcher.RunOnceAsync(CancellationToken.None)).Should().Be(2);

        _producer.Published.Select(m => Event(m).FinalStatus).Should().Equal("Paid", "Reversed");
        (await PendingAsync("C1")).Should().BeEmpty();
    }

    [Fact]
    public async Task ARetriedEvent_KeepsItsEventId()
    {
        await SeedApprovedAsync("C1");
        _producer.Down = true;
        await Finalization().FinalizeAsync("C1", Paid(), Tenant, "actor", "corr");
        var firstTry = Event(_producer.Attempted.Single()).EventId;

        _producer.Down = false;
        _clock.Advance(TimeSpan.FromMinutes(11));
        await _dispatcher.RunOnceAsync(CancellationToken.None);

        Event(_producer.Published.Single()).EventId.Should().Be(firstTry);
    }

    [Fact]
    public async Task WithoutKafkaConfigured_NothingIsQueued()
    {
        _producer.Configured = false;
        await SeedApprovedAsync("C1");

        var result = await Finalization().FinalizeAsync("C1", Paid(), Tenant, "actor", "corr");

        result.Claim!.Status.Should().Be(ClaimStatus.Paid);
        (await _repo.GetForAccumulatorOutboxAsync(Tenant, "C1"))!.PendingClaimEvents.Should().BeNull();
    }

    [Fact]
    public void TheSharedProducer_IsIdempotent_WithAcksAll()
    {
        var config = ChoKafkaProducer.BuildConfig(new ChoKafkaProducerSettings { BootstrapServers = "kafka:9092", ClientId = "claims-service" });

        config.EnableIdempotence.Should().BeTrue();
        config.Acks.Should().Be(Acks.All);
        config.MessageTimeoutMs.Should().Be(10_000);
    }

    private sealed class FakeProducer : IChoEventProducer
    {
        public sealed record Message(string Topic, string Key, string Value, IReadOnlyDictionary<string, string> Headers);

        public bool Configured { get; set; } = true;
        public bool Down { get; set; }
        public List<Message> Published { get; } = new();
        public List<Message> Attempted { get; } = new();
        public bool IsConfigured => Configured;

        public Task ProduceAsync(string topic, string key, string value, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            var message = new Message(topic, key, value, headers);
            Attempted.Add(message);
            if (Down)
                throw new ProduceException<string, string>(new Error(ErrorCode.Local_AllBrokersDown, "all brokers down"), null);
            Published.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
