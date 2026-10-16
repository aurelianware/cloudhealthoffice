using CloudHealthOffice.Infrastructure.Data;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Persistence;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Models;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways.Persistence;

/// <summary>
/// Optimistic concurrency on <see cref="MongoClaimLifecycleStore"/> over real
/// MongoDB (EphemeralMongo). Transmission, inbound attachment receipt and
/// remittance receipt saves used to replace the document unconditionally, so a
/// 277CA, a submit outcome, a post or an outbox dispatch based on an older
/// read silently undid a newer write (a status moved backwards, a published
/// marker was cleared). A save is now conditional on the revision it read, and
/// the background writers re-read and re-apply their change on a conflict.
/// </summary>
public sealed class MongoClaimLifecycleConcurrencyTests(MongoClaimTransmissionTenantTests.Mongod mongo)
    : IClassFixture<MongoClaimTransmissionTenantTests.Mongod>, IAsyncLifetime
{
    private const string Tenant = "tenant-alpha";
    private IMongoDatabase _db = null!;
    private MongoClaimLifecycleStore _store = null!;

    private IClaimTransmissionStore Transmissions => _store;
    private IRemittanceStore Remittances => _store;
    private IInboundClaimAttachmentReceiptStore InboundReceipts => _store;

    public async Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"claim_lifecycle_concurrency_{Guid.NewGuid():N}");
        _store = new MongoClaimLifecycleStore(_db, new ClaimLifecycleOptions());
        await _store.EnsureIndexesAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    // ---- store ---------------------------------------------------------------

    [Fact]
    public async Task Transmission_StaleSave_IsRefused_AndTheNewerWriteStays()
    {
        var tx = await SeedTransmissionAsync();
        var first = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        var second = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;

        first.Status = GatewayClaimTransmissionStatus.AcknowledgmentAccepted;
        await Transmissions.SaveAsync(first);

        second.ErrorMessage = "stale";
        await FluentActions.Awaiting(() => Transmissions.SaveAsync(second))
            .Should().ThrowAsync<ConcurrencyConflictException>();

        var stored = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        stored.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        stored.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Transmission_TwoConcurrentSavesFromTheSameRead_OnlyOneWins()
    {
        var tx = await SeedTransmissionAsync();
        var a = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        var b = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        a.PayerClaimControlNumber = "PCCN-A";
        b.PayerClaimControlNumber = "PCCN-B";

        var results = await Task.WhenAll(
            Capture(() => Transmissions.SaveAsync(a)),
            Capture(() => Transmissions.SaveAsync(b)));

        results.Count(e => e is null).Should().Be(1);
        results.Count(e => e is ConcurrencyConflictException).Should().Be(1);
    }

    [Fact]
    public async Task Transmission_WrittenBeforeRevisionsExisted_SavesOnce_ThenAStaleCopyIsRefused()
    {
        var tx = await SeedTransmissionAsync();
        await _db.GetCollection<BsonDocument>(new ClaimLifecycleOptions().TransmissionsCollection)
            .UpdateOneAsync(new BsonDocument("_id", tx.TransmissionId),
                new BsonDocument("$unset", new BsonDocument { { "Revision", "" }, { "Payload.Revision", "" } }));

        var a = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        var b = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        a.PayerClaimControlNumber = "PCCN-A";
        await Transmissions.SaveAsync(a);

        b.ErrorMessage = "stale";
        await FluentActions.Awaiting(() => Transmissions.SaveAsync(b))
            .Should().ThrowAsync<ConcurrencyConflictException>();
        (await Transmissions.GetByIdAsync(tx.TransmissionId))!.PayerClaimControlNumber.Should().Be("PCCN-A");
    }

    [Fact]
    public async Task Transmission_SaveOfAMissingRecord_StillCreatesIt()
    {
        await Transmissions.SaveAsync(NewTransmission("tx-new"));

        var stored = await Transmissions.GetByTenantAndIdAsync(Tenant, "tx-new");
        stored.Should().NotBeNull();
        await Transmissions.SaveAsync(stored!);
    }

    [Fact]
    public async Task RemittanceReceipt_StaleSave_IsRefused()
    {
        var receipt = await SeedRemittanceAsync();
        var a = (await Remittances.GetByIdAsync(receipt.ReceiptId))!;
        var b = (await Remittances.GetByIdAsync(receipt.ReceiptId))!;
        a.Status = RemittanceLifecycleStatus.Posted;
        await Remittances.SaveAsync(a);

        await FluentActions.Awaiting(() => Remittances.SaveAsync(b))
            .Should().ThrowAsync<ConcurrencyConflictException>();
        (await Remittances.GetByIdAsync(receipt.ReceiptId))!.Status.Should().Be(RemittanceLifecycleStatus.Posted);
    }

    [Fact]
    public async Task InboundAttachmentReceipt_StaleSave_IsRefused()
    {
        var receipt = await SeedInboundReceiptAsync();
        var a = (await InboundReceipts.GetByIdAsync(receipt.ReceiptId))!;
        var b = (await InboundReceipts.GetByIdAsync(receipt.ReceiptId))!;
        a.Outbox.ForEach(e => e.PublishedAtUtc = DateTimeOffset.UtcNow);
        await InboundReceipts.SaveAsync(a);

        await FluentActions.Awaiting(() => InboundReceipts.SaveAsync(b))
            .Should().ThrowAsync<ConcurrencyConflictException>();
        (await InboundReceipts.GetByIdAsync(receipt.ReceiptId))!.HasPendingOutbox.Should().BeFalse();
    }

    // ---- 277CA processor -----------------------------------------------------

    /// <summary>
    /// Two 277CAs for one transmission, both applied to the same read. The
    /// first records the payer claim control number; the second, applied from
    /// the older copy, used to write it back without it.
    /// </summary>
    [Fact]
    public async Task Two277CAs_FromTheSameRead_NeitherIsLost()
    {
        var tx = await SeedTransmissionAsync();
        var stale = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;

        await AckProcessor(Transmissions).ProcessAsync(Ack(tx, "ack-a", ClaimAcknowledgmentStatus.Accepted, "PCCN-A"));
        var later = Ack(tx, "ack-b", ClaimAcknowledgmentStatus.Partial, claimControlNumber: null);
        later.ReceivedAt = DateTimeOffset.Parse("2026-01-15T12:10:00Z");
        await AckProcessor(new StaleReadTransmissions(Transmissions, stale)).ProcessAsync(later);

        var stored = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        stored.PayerClaimControlNumber.Should().Be("PCCN-A");
        stored.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentPartial);
        stored.AcknowledgedAtUtc.Should().Be(later.ReceivedAt);
    }

    /// <summary>
    /// A 277CA accepts the transmission while a dispatcher replays a malformed
    /// 277CA from a read taken before it. The state machine says a malformed
    /// acknowledgment cannot change an outcome, but the stale replay used to
    /// write <c>AcknowledgmentFailed</c> over <c>AcknowledgmentAccepted</c>.
    /// </summary>
    [Fact]
    public async Task Accepted277CA_AndAStaleMalformedReplay_StatusDoesNotMoveBackwards()
    {
        var tx = await SeedTransmissionAsync();
        var stale = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;

        await AckProcessor(Transmissions).ProcessAsync(Ack(tx, "ack-ok", ClaimAcknowledgmentStatus.Accepted, "PCCN-A"));

        var malformedAcks = new InMemoryClaimAcknowledgmentStore();
        await malformedAcks.TryCreateAsync(new ClaimAcknowledgmentRecord
        {
            AcknowledgmentId = "ack-bad",
            Gateway = tx.GatewayName,
            TransmissionId = tx.TransmissionId,
            TenantId = string.Empty,
            Status = ClaimAcknowledgmentStatus.Malformed,
            ReceivedAtUtc = DateTimeOffset.Parse("2026-01-15T12:06:00Z"),
            Outbox = { new ClaimAcknowledgmentOutboxEntry { EventType = "ClaimAcknowledgmentReceived" } }
        });
        await AckProcessor(new StaleReadTransmissions(Transmissions, stale), malformedAcks).DispatchPendingAsync();

        var stored = (await Transmissions.GetByIdAsync(tx.TransmissionId))!;
        stored.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        stored.PayerClaimControlNumber.Should().Be("PCCN-A");
    }

    // ---- gateway submit outcome ---------------------------------------------

    /// <summary>
    /// The gateway accepted the 837 (the 999-level outcome) while a 277CA was
    /// applied to the transmission in between. The submit outcome was saved
    /// from the read taken before the 277CA and moved the status back to
    /// <c>SubmissionAcceptedByGateway</c>, dropping the payer control number.
    /// </summary>
    [Fact]
    public async Task SubmitOutcome_SavedAfterA277CA_DoesNotMoveTheStatusBackwards()
    {
        var interleaving = new InterleavingTransmissions(Transmissions, async (inner, saving) =>
        {
            var current = (await inner.GetByIdAsync(saving.TransmissionId))!;
            current.Status = GatewayClaimTransmissionStatus.AcknowledgmentAccepted;
            current.PayerClaimControlNumber = "PCCN-277CA";
            await inner.SaveAsync(current);
        });
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance, timeProvider: null, transmissions: interleaving);

        var result = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        result.IsSuccess.Should().BeTrue();
        var stored = (await Transmissions.GetByIdAsync(result.Result!.TransmissionId))!;
        stored.Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        stored.PayerClaimControlNumber.Should().Be("PCCN-277CA");
        stored.SubmissionId.Should().NotBeNullOrEmpty("the submit outcome's identifiers are still recorded");
    }

    // ---- 835 processor and poster -------------------------------------------

    /// <summary>
    /// The outbox dispatcher replays a receipt it listed before the remittance
    /// was posted. Its save used to put the receipt back to
    /// <c>AvailableForPosting</c> (so it could be posted again) and drop the
    /// Posted event.
    /// </summary>
    [Fact]
    public async Task StaleOutboxDispatch_DoesNotUndoAPost()
    {
        var receipt = await SeedRemittanceAsync();
        var stale = (await Remittances.GetByIdAsync(receipt.ReceiptId))!;

        var post = await Poster(Remittances).PostAsync(new RemittancePostRequest { TenantId = Tenant, ReceiptId = receipt.ReceiptId });
        post.Status.Should().Be(RemittanceLifecycleStatus.Posted);

        await RemittanceProcessor(new StaleReadRemittances(Remittances, staleList: stale, staleGet: null))
            .DispatchPendingAsync();

        var stored = (await Remittances.GetByIdAsync(receipt.ReceiptId))!;
        stored.Status.Should().Be(RemittanceLifecycleStatus.Posted);
        stored.PostedAtUtc.Should().NotBeNull();
        stored.Outbox.Should().Contain(e => e.EventType == RemittanceMessageTypes.Posted);
        stored.Outbox.Single(e => e.EventType == RemittanceMessageTypes.Received).PublishedAtUtc.Should().NotBeNull();
    }

    /// <summary>
    /// The dispatcher publishes the Received event while a post based on an
    /// earlier read is in flight. The post's save used to clear the published
    /// marker, so the event went out again.
    /// </summary>
    [Fact]
    public async Task OutboxMarker_SurvivesAConcurrentPost()
    {
        var receipt = await SeedRemittanceAsync();
        var stale = (await Remittances.GetByIdAsync(receipt.ReceiptId))!;

        await RemittanceProcessor(Remittances).DispatchPendingAsync();
        (await Remittances.GetByIdAsync(receipt.ReceiptId))!.HasPendingOutbox.Should().BeFalse();

        var post = await Poster(new StaleReadRemittances(Remittances, staleList: null, staleGet: stale))
            .PostAsync(new RemittancePostRequest { TenantId = Tenant, ReceiptId = receipt.ReceiptId });

        post.Status.Should().Be(RemittanceLifecycleStatus.Posted);
        var stored = (await Remittances.GetByIdAsync(receipt.ReceiptId))!;
        stored.Status.Should().Be(RemittanceLifecycleStatus.Posted);
        stored.Outbox.Single(e => e.EventType == RemittanceMessageTypes.Received).PublishedAtUtc.Should().NotBeNull();
        stored.Outbox.Single(e => e.EventType == RemittanceMessageTypes.Posted).PublishedAtUtc.Should().BeNull();
    }

    // ---- inbound attachment receipts ----------------------------------------

    /// <summary>
    /// One replica publishes an inbound attachment receipt's events while
    /// another, from an earlier read, fails to publish them. The failing
    /// replica's save used to mark them unpublished again.
    /// </summary>
    [Fact]
    public async Task InboundAttachmentOutboxMarkers_SurviveAConcurrentFailedDispatch()
    {
        var receipt = await SeedInboundReceiptAsync();
        var stale = (await InboundReceipts.GetByIdAsync(receipt.ReceiptId))!;

        var bus = new CapturingMessageBus();
        await Receiver(InboundReceipts, bus).DispatchPendingAsync(50);
        bus.Sent.Should().HaveCount(2);

        await Receiver(new StaleListInboundReceipts(InboundReceipts, stale), new FailThenCaptureMessageBus(10))
            .DispatchPendingAsync(50);

        var stored = (await InboundReceipts.GetByIdAsync(receipt.ReceiptId))!;
        stored.HasPendingOutbox.Should().BeFalse();
        stored.Outbox.Should().OnlyContain(e => e.PublishedAtUtc != null && e.LastError == null);
    }

    // ---- helpers -------------------------------------------------------------

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static ClaimTransmissionRecord NewTransmission(string id) => new()
    {
        TransmissionId = id,
        TenantId = Tenant,
        ClaimId = "CLM-1",
        GatewayName = "Stedi",
        ClaimType = GatewayClaimType.Professional,
        Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
        IdempotencyKey = $"key-{id}",
        SubmissionId = $"sub-{id}",
        ExternalTransactionId = $"sub-{id}",
        PatientControlNumber = "CLM-1",
        SubmittedAtUtc = DateTimeOffset.Parse("2026-01-15T12:00:00Z")
    };

    private async Task<ClaimTransmissionRecord> SeedTransmissionAsync()
    {
        var (_, record) = await Transmissions.TryCreateAsync(NewTransmission($"tx-{Guid.NewGuid():N}"));
        return record;
    }

    private async Task<RemittanceReceipt> SeedRemittanceAsync()
    {
        var (_, record) = await Remittances.TryCreateAsync(new RemittanceReceipt
        {
            RemittanceId = $"era-{Guid.NewGuid():N}",
            Gateway = "Stedi",
            TenantId = Tenant,
            PaymentAmount = 80m,
            ReceivedAtUtc = DateTimeOffset.Parse("2026-01-20T09:00:00Z"),
            Status = RemittanceLifecycleStatus.AvailableForPosting,
            Claims =
            {
                new RemittedClaim
                {
                    ClaimId = "CLM-1",
                    PaidAmount = 80m,
                    MatchStatus = RemittanceClaimMatchStatus.Matched
                }
            },
            Outbox =
            {
                new RemittanceOutboxEntry
                {
                    EventType = RemittanceMessageTypes.Received,
                    CreatedAtUtc = DateTimeOffset.Parse("2026-01-20T09:00:00Z")
                }
            }
        });
        return record;
    }

    private async Task<InboundClaimAttachmentReceipt> SeedInboundReceiptAsync()
    {
        var (_, record) = await InboundReceipts.TryCreateAsync(new InboundClaimAttachmentReceipt
        {
            IdempotencyKey = $"inbound-{Guid.NewGuid():N}",
            TenantId = Tenant,
            ClaimId = "CLM-1",
            SourceAdapter = "canonical",
            Status = InboundClaimAttachmentStatus.AvailableToClaim,
            ReceivedAtUtc = DateTimeOffset.Parse("2026-01-20T09:00:00Z"),
            Outbox =
            {
                new InboundAttachmentOutboxEntry { EventType = InboundClaimAttachmentMessageTypes.Received },
                new InboundAttachmentOutboxEntry { EventType = InboundClaimAttachmentMessageTypes.Matched }
            }
        });
        return record;
    }

    private static GatewayClaimAcknowledgment Ack(
        ClaimTransmissionRecord tx, string id, ClaimAcknowledgmentStatus status, string? claimControlNumber) => new()
    {
        AcknowledgmentId = id,
        Gateway = tx.GatewayName,
        TransmissionId = tx.TransmissionId,
        ReceivedAt = DateTimeOffset.Parse("2026-01-15T12:05:00Z"),
        Status = status,
        ClaimControlNumber = claimControlNumber,
        ExternalTransactionId = id
    };

    private static ClaimAcknowledgmentProcessor AckProcessor(
        IClaimTransmissionStore transmissions, IClaimAcknowledgmentStore? acks = null) =>
        new(acks ?? new InMemoryClaimAcknowledgmentStore(), transmissions,
            NullLogger<ClaimAcknowledgmentProcessor>.Instance);

    private RemittanceProcessor RemittanceProcessor(IRemittanceStore receipts) =>
        new(receipts, Transmissions, NullLogger<RemittanceProcessor>.Instance);

    private RemittancePoster Poster(IRemittanceStore receipts) =>
        new(receipts, Transmissions, new InMemoryClaimRemittancePostingSink(),
            new InMemoryRemittanceAccumulatorSink(), NullLogger<RemittancePoster>.Instance);

    private static CloudHealthOfficeClaimAttachmentReceiver Receiver(
        IInboundClaimAttachmentReceiptStore receipts, CloudHealthOffice.Infrastructure.Messaging.IMessageBus bus) =>
        // Dispatch uses only the receipt store and the bus.
        new(null!, null!, null!, receipts, NullLogger<CloudHealthOfficeClaimAttachmentReceiver>.Instance, bus: bus);

    /// <summary>Returns an older copy for the first lookup by id (the read a concurrent writer acted on).</summary>
    private sealed class StaleReadTransmissions(IClaimTransmissionStore inner, ClaimTransmissionRecord stale)
        : IClaimTransmissionStore
    {
        private ClaimTransmissionRecord? _stale = stale;

        public Task<ClaimTransmissionRecord?> GetByIdAsync(string transmissionId, CancellationToken ct = default)
        {
            var s = Interlocked.Exchange(ref _stale, null);
            return s is not null && s.TransmissionId == transmissionId
                ? Task.FromResult<ClaimTransmissionRecord?>(s)
                : inner.GetByIdAsync(transmissionId, ct);
        }

        public Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default) => inner.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
        public Task<ClaimTransmissionRecord?> GetByTenantAndIdAsync(string tenantId, string transmissionId, CancellationToken ct = default) => inner.GetByTenantAndIdAsync(tenantId, transmissionId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindBySubmissionIdAsync(string gatewayName, string submissionId, CancellationToken ct = default) => inner.FindBySubmissionIdAsync(gatewayName, submissionId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByExternalTransactionIdAsync(string gatewayName, string externalTransactionId, CancellationToken ct = default) => inner.FindByExternalTransactionIdAsync(gatewayName, externalTransactionId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPatientControlNumberAsync(string gatewayName, string patientControlNumber, CancellationToken ct = default) => inner.FindByPatientControlNumberAsync(gatewayName, patientControlNumber, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPayerClaimControlNumberAsync(string gatewayName, string payerClaimControlNumber, CancellationToken ct = default) => inner.FindByPayerClaimControlNumberAsync(gatewayName, payerClaimControlNumber, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByCorrelationIdAsync(string gatewayName, string correlationId, CancellationToken ct = default) => inner.FindByCorrelationIdAsync(gatewayName, correlationId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByTenantAndClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default) => inner.FindByTenantAndClaimIdAsync(tenantId, claimId, ct);
        public Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default) => inner.SaveAsync(record, ct);
        public Task<(bool Created, ClaimTransmissionRecord Record)> TryCreateAsync(ClaimTransmissionRecord record, CancellationToken ct = default) => inner.TryCreateAsync(record, ct);
    }

    /// <summary>Runs <paramref name="interleave"/> (another writer) just before the first save goes through.</summary>
    private sealed class InterleavingTransmissions(
        IClaimTransmissionStore inner, Func<IClaimTransmissionStore, ClaimTransmissionRecord, Task> interleave)
        : IClaimTransmissionStore
    {
        private int _saves;

        public async Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _saves) == 1)
            {
                await interleave(inner, record);
            }

            await inner.SaveAsync(record, ct);
        }

        public Task<ClaimTransmissionRecord?> GetByIdAsync(string transmissionId, CancellationToken ct = default) => inner.GetByIdAsync(transmissionId, ct);
        public Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default) => inner.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
        public Task<ClaimTransmissionRecord?> GetByTenantAndIdAsync(string tenantId, string transmissionId, CancellationToken ct = default) => inner.GetByTenantAndIdAsync(tenantId, transmissionId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindBySubmissionIdAsync(string gatewayName, string submissionId, CancellationToken ct = default) => inner.FindBySubmissionIdAsync(gatewayName, submissionId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByExternalTransactionIdAsync(string gatewayName, string externalTransactionId, CancellationToken ct = default) => inner.FindByExternalTransactionIdAsync(gatewayName, externalTransactionId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPatientControlNumberAsync(string gatewayName, string patientControlNumber, CancellationToken ct = default) => inner.FindByPatientControlNumberAsync(gatewayName, patientControlNumber, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPayerClaimControlNumberAsync(string gatewayName, string payerClaimControlNumber, CancellationToken ct = default) => inner.FindByPayerClaimControlNumberAsync(gatewayName, payerClaimControlNumber, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByCorrelationIdAsync(string gatewayName, string correlationId, CancellationToken ct = default) => inner.FindByCorrelationIdAsync(gatewayName, correlationId, ct);
        public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByTenantAndClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default) => inner.FindByTenantAndClaimIdAsync(tenantId, claimId, ct);
        public Task<(bool Created, ClaimTransmissionRecord Record)> TryCreateAsync(ClaimTransmissionRecord record, CancellationToken ct = default) => inner.TryCreateAsync(record, ct);
    }

    /// <summary>Hands out an older copy once, from the pending-outbox list or the first lookup by id.</summary>
    private sealed class StaleReadRemittances(IRemittanceStore inner, RemittanceReceipt? staleList, RemittanceReceipt? staleGet)
        : IRemittanceStore
    {
        private RemittanceReceipt? _staleList = staleList;
        private RemittanceReceipt? _staleGet = staleGet;

        public Task<IReadOnlyList<RemittanceReceipt>> ListPendingOutboxAsync(int take, CancellationToken ct = default)
        {
            var s = Interlocked.Exchange(ref _staleList, null);
            return s is not null
                ? Task.FromResult<IReadOnlyList<RemittanceReceipt>>(new[] { s })
                : inner.ListPendingOutboxAsync(take, ct);
        }

        public Task<RemittanceReceipt?> GetByIdAsync(string receiptId, CancellationToken ct = default)
        {
            var s = Interlocked.Exchange(ref _staleGet, null);
            return s is not null ? Task.FromResult<RemittanceReceipt?>(s) : inner.GetByIdAsync(receiptId, ct);
        }

        public Task<RemittanceReceipt?> GetByIdempotencyKeyAsync(string gateway, string remittanceId, CancellationToken ct = default) => inner.GetByIdempotencyKeyAsync(gateway, remittanceId, ct);
        public Task<RemittanceReceipt?> GetByEventIdAsync(string gateway, string eventId, CancellationToken ct = default) => inner.GetByEventIdAsync(gateway, eventId, ct);
        public Task<IReadOnlyList<RemittanceReceipt>> ListByTransmissionIdAsync(string transmissionId, CancellationToken ct = default) => inner.ListByTransmissionIdAsync(transmissionId, ct);
        public Task<IReadOnlyList<RemittanceReceipt>> ListByTenantAsync(string tenantId, int take, CancellationToken ct = default) => inner.ListByTenantAsync(tenantId, take, ct);
        public Task SaveAsync(RemittanceReceipt record, CancellationToken ct = default) => inner.SaveAsync(record, ct);
        public Task<(bool Created, RemittanceReceipt Record)> TryCreateAsync(RemittanceReceipt record, CancellationToken ct = default) => inner.TryCreateAsync(record, ct);
        public Task<IReadOnlyList<RemittanceReceipt>> ListAvailableForPostingAsync(string tenantId, int take, CancellationToken ct = default) => inner.ListAvailableForPostingAsync(tenantId, take, ct);
    }

    /// <summary>Hands out an older copy once from the pending-outbox list.</summary>
    private sealed class StaleListInboundReceipts(IInboundClaimAttachmentReceiptStore inner, InboundClaimAttachmentReceipt stale)
        : IInboundClaimAttachmentReceiptStore
    {
        private InboundClaimAttachmentReceipt? _stale = stale;

        public Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListPendingOutboxAsync(int take, CancellationToken ct = default)
        {
            var s = Interlocked.Exchange(ref _stale, null);
            return s is not null
                ? Task.FromResult<IReadOnlyList<InboundClaimAttachmentReceipt>>(new[] { s })
                : inner.ListPendingOutboxAsync(take, ct);
        }

        public Task<InboundClaimAttachmentReceipt?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default) => inner.GetByIdempotencyKeyAsync(idempotencyKey, ct);
        public Task<InboundClaimAttachmentReceipt?> GetByIdAsync(string receiptId, CancellationToken ct = default) => inner.GetByIdAsync(receiptId, ct);
        public Task<IReadOnlyList<InboundClaimAttachmentReceipt>> ListByClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default) => inner.ListByClaimIdAsync(tenantId, claimId, ct);
        public Task SaveAsync(InboundClaimAttachmentReceipt record, CancellationToken ct = default) => inner.SaveAsync(record, ct);
        public Task<(bool Created, InboundClaimAttachmentReceipt Record)> TryCreateAsync(InboundClaimAttachmentReceipt record, CancellationToken ct = default) => inner.TryCreateAsync(record, ct);
    }
}
