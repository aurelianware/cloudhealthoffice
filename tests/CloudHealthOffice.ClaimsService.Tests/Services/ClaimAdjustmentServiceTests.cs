using ClaimsService.Models;
using ClaimsService.Models.Messaging;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services;

/// <summary>
/// Capability 5.12a — covers <see cref="ClaimAdjustmentService"/>'s
/// happy path + the source-state, depth, idempotency, and chain-conflict
/// branches; verifies the dual version-event emission
/// (<c>ClaimVersionSuperseded</c> + <c>ClaimVersionReversed</c>) and the
/// Service Bus <c>ClaimVersionReversedMessage</c> emit; verifies the
/// fresh-AI-examination semantics (Gap 6 — predecessor's
/// <see cref="Claim.AiExamination"/> snapshot is not carried over).
/// </summary>
public class ClaimAdjustmentServiceTests
{
    private readonly IClaimRepository _claimRepository = Substitute.For<IClaimRepository>();
    private readonly IClaimAdjustmentRepository _adjustmentRepository = Substitute.For<IClaimAdjustmentRepository>();
    private readonly IClaimSubmissionService _submissionService = Substitute.For<IClaimSubmissionService>();
    private readonly IClaimVersionEventPublisher _versionPublisher = Substitute.For<IClaimVersionEventPublisher>();
    private readonly IMessageBus _messageBus = Substitute.For<IMessageBus>();

    private ClaimAdjustmentService CreateService() => new(
        _claimRepository,
        _adjustmentRepository,
        _submissionService,
        _versionPublisher,
        _messageBus,
        NullLogger<ClaimAdjustmentService>.Instance);

    private static Claim PaidPredecessor(string id = "pred-1", string tenantId = "t1") => new()
    {
        Id = id,
        TenantId = tenantId,
        ClaimVersionId = id,
        VersionNumber = 1,
        VersionState = ClaimVersionState.Paid,
        Status = ClaimStatus.Paid,
        ClaimNumber = "CLM-001",
        MemberId = "m1",
        BillingProviderNPI = "1234567890",
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 1000m,
        AdjudicationResult = new AdjudicationResult { CheckNumber = "CHK-001", PayerPayment = 800m },
        AiExamination = new AiExamination { Rationale = "stale-pre-adjustment" },
    };

    private static AdapterClaim CorrectedAdapterClaim(string memberId = "m1") => new()
    {
        TenantId = "ignored-overridden",
        Id = "ignored-overridden",
        ClaimVersionId = "ignored-overridden",
        VersionNumber = 999,
        VersionState = ClaimVersionState.Adjudicated,
        PredecessorVersionId = "ignored-overridden",
        ClaimNumber = "CLM-001",
        MemberId = memberId,
        BillingProviderNPI = "1234567890",
        ServiceDateFrom = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        TotalChargeAmount = 1200m,
        Status = ClaimStatus.Approved,
        AiExamination = new AiExamination { Rationale = "should-be-zeroed" },
        PendDetails = new PendDetails { },
        AdjudicationResult = new AdapterAdjudicationResult { PayerPayment = 1100m },
        ClaimLines = new List<AdapterClaimLine>
        {
            new() { ProcedureCode = "99213", ChargeAmount = 1200m, Units = 1 }
        },
    };

    private static ClaimAdjustmentRequest Request(string reason = "Wrong service code") => new()
    {
        AdjustmentReason = reason,
        Notes = "operator note",
        CorrectedClaim = CorrectedAdapterClaim(),
    };

    private void StubSubmissionSuccess(string newClaimId = "new-1", int newVersionNumber = 2, string chainKey = "pred-1")
    {
        _submissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<AdapterClaim>();
                c.Id = newClaimId;
                c.ClaimVersionId = chainKey;
                c.VersionNumber = newVersionNumber;
                c.VersionState = ClaimVersionState.Submitted;
                return Task.FromResult(ClaimSubmissionResult.Ok(c));
            });
    }

    [Fact]
    public async Task CreateAdjustment_HappyPath_PersistsAggregateAndEmitsAllSignals()
    {
        var predecessor = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(predecessor);
        _claimRepository.MarkSupersededProjectionAsync("t1", "pred-1", Arg.Any<string>(), Arg.Any<DateTime>(), "actor-1", Arg.Any<CancellationToken>())
            .Returns(true);
        StubSubmissionSuccess();

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.Created, result.Outcome);
        Assert.NotNull(result.Adjustment);
        Assert.Equal(ClaimAdjustmentStatus.AwaitingReadjudication, result.Adjustment!.Status);
        Assert.Equal("pred-1", result.Adjustment.PredecessorClaimId);
        Assert.Equal("new-1", result.Adjustment.NewClaimId);
        Assert.Equal("pred-1", result.Adjustment.ClaimVersionId);

        await _claimRepository.Received(1).MarkSupersededProjectionAsync(
            "t1", "pred-1", "new-1", Arg.Any<DateTime>(), "actor-1", Arg.Any<CancellationToken>());
        await _versionPublisher.Received(1).PublishVersionSupersededAsync(
            Arg.Any<Claim>(), Arg.Any<Claim>(), "Wrong service code", "actor-1", "corr-1", Arg.Any<CancellationToken>());
        await _versionPublisher.Received(1).PublishVersionReversedAsync(
            Arg.Any<Claim>(), "new-1", "Wrong service code", "actor-1", "corr-1", Arg.Any<CancellationToken>());
        await _messageBus.Received(1).SendAsync(
            "claim-version-events",
            Arg.Any<ClaimVersionReversedMessage>(),
            Arg.Any<SendOptions?>(),
            Arg.Any<CancellationToken>());
        await _adjustmentRepository.Received(1).CreateAsync(Arg.Any<ClaimAdjustment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAdjustment_OverridesIdentityFieldsAndZerosStaleSignals()
    {
        var predecessor = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(predecessor);
        _claimRepository.MarkSupersededProjectionAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(true);

        // Snapshot the as-handed-off-to-SubmitAsync state BEFORE the stub
        // simulates the adapter assigning a new id. AdapterClaim is a class
        // (reference type), so the service's pre-submit mutations are visible
        // to whatever the stub later writes — snapshot at hand-off time.
        AdapterClaim? snapshot = null;
        _submissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<AdapterClaim>();
                snapshot = AdapterClaim.From(c.ToClaim());
                c.Id = "new-1";
                return Task.FromResult(ClaimSubmissionResult.Ok(c));
            });

        var sut = CreateService();
        await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.NotNull(snapshot);
        Assert.Equal("t1", snapshot!.TenantId);
        Assert.Equal(string.Empty, snapshot.Id); // CreateAsync inside adapter assigns id
        Assert.Equal("pred-1", snapshot.ClaimVersionId);
        Assert.Equal(2, snapshot.VersionNumber);
        Assert.Equal(ClaimVersionState.Submitted, snapshot.VersionState);
        Assert.Equal("pred-1", snapshot.PredecessorVersionId);
        Assert.Equal(ClaimStatus.Submitted, snapshot.Status);
        // Gap 6 — fresh AI examination; predecessor's snapshot must not leak
        Assert.Null(snapshot.AiExamination);
        Assert.Null(snapshot.PendDetails);
        Assert.Null(snapshot.AdjudicationResult);
        Assert.Null(snapshot.PaidDate);
        Assert.Null(snapshot.AdjudicatedDate);
    }

    [Fact]
    public async Task CreateAdjustment_SameIdempotencyKey_SameBody_ReturnsAlreadyExists()
    {
        // The replay scenario tests hash *value* equality, not object
        // identity. Construct an existing ClaimAdjustment whose RequestHash
        // matches what the service will compute from the second call's
        // request. Use the same internal hashing helper to guarantee
        // alignment with the service's logic.
        var request = Request();
        var expectedHash = ClaimAdjustmentService.ComputeRequestHash("pred-1", request);
        var existing = new ClaimAdjustment
        {
            TenantId = "t1",
            Id = "adj-existing",
            ClaimVersionId = "pred-1",
            PredecessorClaimId = "pred-1",
            PredecessorVersionId = "pred-1",
            NewClaimId = "new-from-prior-call",
            AdjustmentReason = "Wrong service code",
            Status = ClaimAdjustmentStatus.AwaitingReadjudication,
            IdempotencyKey = "idem-1",
            RequestHash = expectedHash,
            CreatedBy = "actor-1",
        };
        _adjustmentRepository.GetByIdempotencyKeyAsync("t1", "idem-1", Arg.Any<CancellationToken>()).Returns(existing);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", request, "idem-1", "t1", "actor-1", "corr-replay");

        Assert.Equal(ClaimAdjustmentOutcome.AlreadyExists, result.Outcome);
        Assert.Same(existing, result.Adjustment);
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().CreateAsync(default!, default);
    }

    [Fact]
    public async Task CreateAdjustment_SameIdempotencyKey_DifferentBody_ReturnsConflict()
    {
        var stale = new ClaimAdjustment
        {
            TenantId = "t1",
            ClaimVersionId = "pred-1",
            PredecessorClaimId = "pred-1",
            PredecessorVersionId = "pred-1",
            NewClaimId = "new-1",
            AdjustmentReason = "DIFFERENT REASON",
            Status = ClaimAdjustmentStatus.AwaitingReadjudication,
            IdempotencyKey = "idem-1",
            RequestHash = "OLD_HASH_DIFFERENT",
            CreatedBy = "actor-0",
        };
        _adjustmentRepository.GetByIdempotencyKeyAsync("t1", "idem-1", Arg.Any<CancellationToken>()).Returns(stale);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request("Wrong service code"), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.IdempotencyConflict, result.Outcome);
        Assert.Same(stale, result.Adjustment);
    }

    [Fact]
    public async Task CreateAdjustment_PredecessorNotFound_Returns404()
    {
        _claimRepository.GetByIdAsync("missing").Returns((Claim?)null);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "missing", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.PredecessorNotFound, result.Outcome);
    }

    [Fact]
    public async Task CreateAdjustment_PredecessorWrongTenant_Returns404()
    {
        var p = PaidPredecessor(tenantId: "OTHER");
        _claimRepository.GetByIdAsync("pred-1").Returns(p);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.PredecessorNotFound, result.Outcome);
    }

    [Theory]
    [InlineData(ClaimStatus.Submitted)]
    [InlineData(ClaimStatus.Pended)]
    [InlineData(ClaimStatus.InAdjudication)]
    [InlineData(ClaimStatus.Approved)]
    [InlineData(ClaimStatus.Voided)]
    public async Task CreateAdjustment_InvalidSourceState_Returns422(ClaimStatus status)
    {
        var p = PaidPredecessor();
        p.Status = status;
        _claimRepository.GetByIdAsync("pred-1").Returns(p);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.InvalidSourceState, result.Outcome);
    }

    [Theory]
    [InlineData(ClaimStatus.Paid)]
    [InlineData(ClaimStatus.Denied)]
    [InlineData(ClaimStatus.PartiallyPaid)]
    public async Task CreateAdjustment_AcceptedSourceStates_DoNotReturnInvalidSourceState(ClaimStatus status)
    {
        var p = PaidPredecessor();
        p.Status = status;
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        _claimRepository.MarkSupersededProjectionAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(true);
        StubSubmissionSuccess();

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), $"idem-{status}", "t1", "actor-1", "corr-1");

        Assert.NotEqual(ClaimAdjustmentOutcome.InvalidSourceState, result.Outcome);
    }

    [Fact]
    public async Task CreateAdjustment_DepthLimitExceeded_Returns422()
    {
        var p = PaidPredecessor();
        p.PredecessorVersionId = "earlier-version";  // predecessor is itself an adjustment
        _claimRepository.GetByIdAsync("pred-1").Returns(p);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.DepthLimitExceeded, result.Outcome);
    }

    [Fact]
    public async Task CreateAdjustment_ChainHasInflightAdjustment_Returns409()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        var existing = new ClaimAdjustment
        {
            TenantId = "t1",
            ClaimVersionId = "pred-1",
            PredecessorClaimId = "pred-1",
            PredecessorVersionId = "pred-1",
            NewClaimId = "new-0",
            Status = ClaimAdjustmentStatus.PendingReversal,
            IdempotencyKey = "idem-old",
            RequestHash = "h",
            CreatedBy = "actor-0",
            AdjustmentReason = "earlier",
        };
        _adjustmentRepository.GetByClaimVersionIdAsync("t1", "pred-1", Arg.Any<CancellationToken>()).Returns(existing);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-new", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.ConflictingAdjustment, result.Outcome);
        Assert.Same(existing, result.Adjustment);
    }

    [Fact]
    public async Task CreateAdjustment_SubmissionFails_ReturnsSubmissionFailedAndReleasesChainLock()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        var errors = new[] { new ValidationError { Field = "MemberId", Code = "Required", Message = "required" } };
        _submissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ClaimSubmissionResult.ValidationFailed(errors)));
        _adjustmentRepository.DeleteAsync(default!, default!, default).ReturnsForAnyArgs(true);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.SubmissionFailed, result.Outcome);
        Assert.Equal(ClaimSubmissionFailureKind.Validation, result.SubmissionFailureKind);
        Assert.Single(result.SubmissionErrors);
        await _claimRepository.DidNotReceiveWithAnyArgs().MarkSupersededProjectionAsync(
            default!, default!, default!, default, default, default);
        // Placeholder was inserted (chain lock acquired) then released on failure.
        await _adjustmentRepository.Received(1).CreateAsync(Arg.Any<ClaimAdjustment>(), Arg.Any<CancellationToken>());
        await _adjustmentRepository.Received(1).DeleteAsync("t1", Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Final-stage UpdateAsync (NewClaimId set) must NOT have run.
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task CreateAdjustment_AdapterNotImplemented_Returns501FailureKind()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        _submissionService
            .SubmitAsync(Arg.Any<AdapterClaim>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ClaimSubmissionResult.AdapterNotImplemented("vendor stub")));
        _adjustmentRepository.DeleteAsync(default!, default!, default).ReturnsForAnyArgs(true);

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.SubmissionFailed, result.Outcome);
        Assert.Equal(ClaimSubmissionFailureKind.NotImplemented, result.SubmissionFailureKind);
    }

    [Fact]
    public async Task CreateAdjustment_VersionPublisherThrows_StillCreatesAdjustment()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        _claimRepository.MarkSupersededProjectionAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(true);
        StubSubmissionSuccess();
        _versionPublisher
            .PublishVersionSupersededAsync(default!, default!, default, default, default, default)
            .ReturnsForAnyArgs<ClaimVersionEvent>(_ => throw new InvalidOperationException("Mongo down"));

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.Created, result.Outcome);
        await _adjustmentRepository.Received(1).CreateAsync(Arg.Any<ClaimAdjustment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAdjustment_ServiceBusEmitFails_StillCreatesAdjustment()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        _claimRepository.MarkSupersededProjectionAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(true);
        StubSubmissionSuccess();
        _messageBus
            .SendAsync(Arg.Any<string>(), Arg.Any<ClaimVersionReversedMessage>(), Arg.Any<SendOptions?>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Service Bus down"));

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task CreateAdjustment_SupersessionFails_ReturnsSubmissionFailedAndReleasesChainLock()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        _claimRepository.MarkSupersededProjectionAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(false);
        _adjustmentRepository.DeleteAsync(default!, default!, default).ReturnsForAnyArgs(true);
        StubSubmissionSuccess();

        var sut = CreateService();
        var result = await sut.CreateAdjustmentAsync(
            "pred-1", Request(), "idem-1", "t1", "actor-1", "corr-1");

        Assert.Equal(ClaimAdjustmentOutcome.SubmissionFailed, result.Outcome);
        // Placeholder was inserted (chain lock acquired) then released.
        await _adjustmentRepository.Received(1).CreateAsync(Arg.Any<ClaimAdjustment>(), Arg.Any<CancellationToken>());
        await _adjustmentRepository.Received(1).DeleteAsync("t1", Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Final-stage UpdateAsync (NewClaimId set) must NOT have run.
        await _adjustmentRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task CreateAdjustment_RejectsBlankIdempotencyKey()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateAdjustmentAsync("pred-1", Request(), "", "t1", "actor-1", "corr-1"));
    }

    [Fact]
    public async Task CreateAdjustment_RejectsBlankActorId()
    {
        var sut = CreateService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            sut.CreateAdjustmentAsync("pred-1", Request(), "idem-1", "t1", "", "corr-1"));
    }

    [Fact]
    public async Task CreateAdjustment_ServiceBusMessageCarriesCorrectMetadata()
    {
        var p = PaidPredecessor();
        _claimRepository.GetByIdAsync("pred-1").Returns(p);
        _claimRepository.MarkSupersededProjectionAsync(default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(true);
        StubSubmissionSuccess(newClaimId: "new-7");

        ClaimVersionReversedMessage? captured = null;
        SendOptions? capturedOptions = null;
        _messageBus
            .When(b => b.SendAsync(
                Arg.Any<string>(),
                Arg.Any<ClaimVersionReversedMessage>(),
                Arg.Any<SendOptions?>(),
                Arg.Any<CancellationToken>()))
            .Do(ci =>
            {
                captured = ci.Arg<ClaimVersionReversedMessage>();
                capturedOptions = ci.Arg<SendOptions?>();
            });

        var sut = CreateService();
        await sut.CreateAdjustmentAsync(
            "pred-1", Request("Charged wrong code"), "idem-1", "t1", "actor-1", "corr-99");

        Assert.NotNull(captured);
        Assert.Equal("t1", captured!.TenantId);
        Assert.Equal("pred-1", captured.ClaimId);
        Assert.Equal("pred-1", captured.PredecessorVersionId);
        Assert.Equal("new-7", captured.SupersessorClaimId);
        Assert.Equal("Charged wrong code", captured.AdjustmentReason);
        Assert.Equal("actor-1", captured.ActorId);
        Assert.Equal("corr-99", captured.CorrelationId);

        Assert.NotNull(capturedOptions);
        Assert.Equal("reversed:pred-1->new-7", capturedOptions!.MessageId);
        Assert.Equal("ClaimVersionReversed", capturedOptions.Properties!["MessageType"]);
    }
}
