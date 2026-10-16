using System.Net;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Persistence;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways.Persistence;
using CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

/// <summary>
/// A submit for an idempotency key whose transmission is still
/// <c>Transmitting</c> (another request is sending it right now) used to send
/// the claim again: both the request that found the record and the loser of
/// the create race treated <c>Transmitting</c> like <c>Failed</c>. The claim
/// went to the clearinghouse twice. An in-flight transmission is now answered
/// as a replay that is not yet accepted, and is resent only once its
/// <see cref="ClaimTransmissionRecord.TransmittingSinceUtc"/> is older than
/// the in-flight lease (the sender crashed).
/// </summary>
public class ClaimSubmitInFlightTests
{
    internal const string SuccessJson =
        "{\"status\":\"SUCCESS\",\"controlNumber\":\"000000001\"," +
        "\"claimReference\":{\"correlationId\":\"01CLAIMCORR\",\"rhclaimNumber\":\"01CLAIMCORR\",\"patientControlNumber\":\"CLM-P-1001\"}," +
        "\"meta\":{\"traceId\":\"trace-claim-1\"}}";

    internal static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal static StediHealthcareGateway Stedi(StubHttpMessageHandler handler, IClaimTransmissionStore store)
    {
        var opts = Options.Create(new StediGatewayOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox",
            EligibilityPath = "/eligibility/v3",
            ProfessionalClaimPath = "/professionalclaims/v3/submission",
            InstitutionalClaimPath = "/institutionalclaims/v1/submission",
            DentalClaimPath = "/dental-claims/submission",
            MaxRetries = 1
        });
        var factory = new StubHttpClientFactory(handler);
        var eligibility = new StediEligibilityApiClient(
            factory, opts, NullLogger<StediEligibilityApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        var claims = new StediClaimApiClient(
            factory, opts, NullLogger<StediClaimApiClient>.Instance, delay: (_, _) => Task.CompletedTask);
        return new StediHealthcareGateway(
            eligibility, PayerTestHarness.CreateResolver(opts), opts,
            NullLogger<StediHealthcareGateway>.Instance, new FixedTime(Now), claims, store);
    }

    private static MockHealthcareGateway Mock(IClaimTransmissionStore store) =>
        new(NullLogger<MockHealthcareGateway>.Instance, new FixedTime(Now), store);

    internal static async Task<ClaimTransmissionRecord> SeedAsync(
        IClaimTransmissionStore store,
        GatewayClaimTransmissionStatus status,
        DateTimeOffset? transmittingSince,
        string gatewayName)
    {
        var request = GatewayClaimFixtures.Professional();
        var (_, record) = await store.TryCreateAsync(new ClaimTransmissionRecord
        {
            TenantId = request.TenantId,
            ClaimId = request.ClaimId,
            ClaimVersion = request.ClaimVersion,
            ClaimType = request.ClaimType,
            GatewayName = gatewayName,
            IdempotencyKey = request.ResolveIdempotencyKey(),
            Status = status,
            SubmittedAtUtc = Now.AddDays(-2),
            TransmittingSinceUtc = transmittingSince
        });
        return record;
    }

    private static void ShouldBeInFlightReplay(GatewayResponse<GatewayClaimSubmissionResult> response, string transmissionId)
    {
        response.IsSuccess.Should().BeTrue();
        response.Result!.ReplayOfExistingTransmission.Should().BeTrue();
        response.Result.TransmissionId.Should().Be(transmissionId);
        response.Result.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.Transmitting);
        response.Result.AcceptedForProcessing.Should().BeFalse("the other request has not had an answer yet");
        response.Metadata.Status.Should().Be(GatewayTransactionStatus.Submitted);
    }

    // ---- Stedi ---------------------------------------------------------------

    [Fact]
    public async Task Stedi_ATransmissionStillInFlight_IsNotSentAgain()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var store = new InMemoryClaimTransmissionStore();
        var seeded = await SeedAsync(store, GatewayClaimTransmissionStatus.Transmitting, Now.AddSeconds(-30), "stedi");

        var response = await Stedi(handler, store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        handler.CallCount.Should().Be(0, "the claim is already being sent");
        ShouldBeInFlightReplay(response, seeded.TransmissionId);
    }

    [Fact]
    public async Task Stedi_TheLoserOfTheCreateRace_DoesNotSendWhileTheWinnerIsInFlight()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var inner = new InMemoryClaimTransmissionStore();
        var seeded = await SeedAsync(inner, GatewayClaimTransmissionStatus.Transmitting, Now.AddSeconds(-1), "stedi");
        var store = new MissesTheFirstLookup(inner);

        var response = await Stedi(handler, store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        handler.CallCount.Should().Be(0, "the winner of the create race is sending it");
        ShouldBeInFlightReplay(response, seeded.TransmissionId);
    }

    [Fact]
    public async Task Stedi_ATransmissionStuckPastTheLease_IsSentAgain()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var store = new InMemoryClaimTransmissionStore();
        await SeedAsync(store, GatewayClaimTransmissionStatus.Transmitting,
            Now - ClaimLifecycleWrites.InFlightLease - TimeSpan.FromSeconds(1), "stedi");

        var response = await Stedi(handler, store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        handler.CallCount.Should().Be(1, "the sender crashed; the claim was never answered");
        response.Result!.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
    }

    [Fact]
    public async Task Stedi_ALegacyTransmittingRecord_UsesItsSubmittedTimeForTheLease()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, SuccessJson);
        var store = new InMemoryClaimTransmissionStore();
        await SeedAsync(store, GatewayClaimTransmissionStatus.Transmitting, transmittingSince: null, "stedi");

        await Stedi(handler, store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        handler.CallCount.Should().Be(1, "submitted two days ago and never answered");
    }

    [Fact]
    public async Task Stedi_AFailedTransmission_IsStillRetried_AndMarkedInFlightWhileSending()
    {
        var store = new InMemoryClaimTransmissionStore();
        var seeded = await SeedAsync(store, GatewayClaimTransmissionStatus.Failed, Now.AddSeconds(-30), "stedi");
        ClaimTransmissionRecord? duringSend = null;
        var handler = new StubHttpMessageHandler().Enqueue(_ =>
        {
            duringSend = store.GetByIdAsync(seeded.TransmissionId).GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var response = await Stedi(handler, store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        handler.CallCount.Should().Be(1);
        response.Result!.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
        duringSend!.Status.Should().Be(GatewayClaimTransmissionStatus.Transmitting);
        duringSend.TransmittingSinceUtc.Should().Be(Now, "the lease starts when this send starts, not at the first submit");
    }

    // ---- Mock ----------------------------------------------------------------

    [Fact]
    public async Task Mock_ATransmissionStillInFlight_IsNotSentAgain()
    {
        var store = new InMemoryClaimTransmissionStore();
        var seeded = await SeedAsync(store, GatewayClaimTransmissionStatus.Transmitting, Now.AddSeconds(-30), "mock");

        var response = await Mock(store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        ShouldBeInFlightReplay(response, seeded.TransmissionId);
        var stored = (await store.GetByIdAsync(seeded.TransmissionId))!;
        stored.Status.Should().Be(GatewayClaimTransmissionStatus.Transmitting);
        stored.SubmissionId.Should().BeNull("nothing was sent");
    }

    [Fact]
    public async Task Mock_TheLoserOfTheCreateRace_DoesNotSendWhileTheWinnerIsInFlight()
    {
        var inner = new InMemoryClaimTransmissionStore();
        var seeded = await SeedAsync(inner, GatewayClaimTransmissionStatus.Transmitting, Now.AddSeconds(-1), "mock");

        var response = await Mock(new MissesTheFirstLookup(inner)).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        ShouldBeInFlightReplay(response, seeded.TransmissionId);
        (await inner.GetByIdAsync(seeded.TransmissionId))!.SubmissionId.Should().BeNull("nothing was sent");
    }

    [Fact]
    public async Task Mock_ATransmissionStuckPastTheLease_IsSentAgain()
    {
        var store = new InMemoryClaimTransmissionStore();
        var seeded = await SeedAsync(store, GatewayClaimTransmissionStatus.Transmitting,
            Now - ClaimLifecycleWrites.InFlightLease - TimeSpan.FromSeconds(1), "mock");

        var response = await Mock(store).SubmitClaimAsync(GatewayClaimFixtures.Professional());

        response.Result!.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
        (await store.GetByIdAsync(seeded.TransmissionId))!.SubmissionId.Should().NotBeNull();
    }

    /// <summary>
    /// The idempotency lookup misses once, as it does for the second of two
    /// requests that both looked before either created the record; that
    /// request then loses the create race.
    /// </summary>
    private sealed class MissesTheFirstLookup(IClaimTransmissionStore inner) : DelegatingTransmissionStore(inner)
    {
        private bool _missed;

        public override Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default)
        {
            if (_missed)
                return base.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
            _missed = true;
            return Task.FromResult<ClaimTransmissionRecord?>(null);
        }
    }
}

/// <summary>Passes every call through; tests override the idempotency lookup.</summary>
internal class DelegatingTransmissionStore(IClaimTransmissionStore inner) : IClaimTransmissionStore
{
    public virtual Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default) =>
        inner.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);

    public Task<ClaimTransmissionRecord?> GetByIdAsync(string transmissionId, CancellationToken ct = default) =>
        inner.GetByIdAsync(transmissionId, ct);
    public Task<ClaimTransmissionRecord?> GetByTenantAndIdAsync(string tenantId, string transmissionId, CancellationToken ct = default) =>
        inner.GetByTenantAndIdAsync(tenantId, transmissionId, ct);
    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindBySubmissionIdAsync(string gatewayName, string submissionId, CancellationToken ct = default) =>
        inner.FindBySubmissionIdAsync(gatewayName, submissionId, ct);
    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByExternalTransactionIdAsync(string gatewayName, string externalTransactionId, CancellationToken ct = default) =>
        inner.FindByExternalTransactionIdAsync(gatewayName, externalTransactionId, ct);
    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPatientControlNumberAsync(string gatewayName, string patientControlNumber, CancellationToken ct = default) =>
        inner.FindByPatientControlNumberAsync(gatewayName, patientControlNumber, ct);
    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPayerClaimControlNumberAsync(string gatewayName, string payerClaimControlNumber, CancellationToken ct = default) =>
        inner.FindByPayerClaimControlNumberAsync(gatewayName, payerClaimControlNumber, ct);
    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByCorrelationIdAsync(string gatewayName, string correlationId, CancellationToken ct = default) =>
        inner.FindByCorrelationIdAsync(gatewayName, correlationId, ct);
    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByTenantAndClaimIdAsync(string tenantId, string claimId, CancellationToken ct = default) =>
        inner.FindByTenantAndClaimIdAsync(tenantId, claimId, ct);
    public Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default) => inner.SaveAsync(record, ct);
    public Task<(bool Created, ClaimTransmissionRecord Record)> TryCreateAsync(ClaimTransmissionRecord record, CancellationToken ct = default) =>
        inner.TryCreateAsync(record, ct);
}

/// <summary>
/// Two retries of a failed transmission over real MongoDB that both read it
/// before either claimed the send: the second used to get a 409 out of the
/// gateway. Now exactly one sends and the other is answered as an in-flight
/// replay.
/// </summary>
public sealed class ClaimSubmitInFlightMongoTests(MongoClaimTransmissionTenantTests.Mongod mongo)
    : IClassFixture<MongoClaimTransmissionTenantTests.Mongod>, IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private MongoClaimLifecycleStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.Client.GetDatabase($"claim_submit_inflight_{Guid.NewGuid():N}");
        _store = new MongoClaimLifecycleStore(_db, new ClaimLifecycleOptions());
        await _store.EnsureIndexesAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_db.DatabaseNamespace.DatabaseName);

    [Fact]
    public async Task TwoRetriesOfAFailedTransmission_FromTheSameRead_SendOnce()
    {
        var seeded = await ClaimSubmitInFlightTests.SeedAsync(
            _store, GatewayClaimTransmissionStatus.Failed, transmittingSince: null, "stedi");
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, ClaimSubmitInFlightTests.SuccessJson)
            .EnqueueJson(HttpStatusCode.OK, ClaimSubmitInFlightTests.SuccessJson);
        var store = new BothLookUpBeforeEitherWrites(_store);
        var gateway = ClaimSubmitInFlightTests.Stedi(handler, store);

        var responses = await Task.WhenAll(
            Task.Run(() => gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional())),
            Task.Run(() => gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional())));

        handler.CallCount.Should().Be(1, "only the request that claimed the send sends");
        responses.Should().OnlyContain(r => r.IsSuccess);
        responses.Count(r => r.Result!.ReplayOfExistingTransmission).Should().Be(1);
        responses.Select(r => r.Result!.TransmissionId).Distinct().Should().Equal(seeded.TransmissionId);
        (await ((IClaimTransmissionStore)_store).GetByIdAsync(seeded.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
    }

    private sealed class BothLookUpBeforeEitherWrites(IClaimTransmissionStore inner) : DelegatingTransmissionStore(inner)
    {
        private readonly Barrier _both = new(2);

        public override async Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(
            string tenantId, string idempotencyKey, CancellationToken ct = default)
        {
            var record = await base.GetByIdempotencyKeyAsync(tenantId, idempotencyKey, ct);
            _both.SignalAndWait(TimeSpan.FromSeconds(10));
            return record;
        }
    }
}
