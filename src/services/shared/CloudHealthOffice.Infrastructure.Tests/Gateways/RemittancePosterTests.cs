using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class RemittancePosterTests
{
    [Fact]
    public async Task AvailableForPosting_PostsClaimAndAccumulators_WithoutChanging277caOrTransmission()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");

        var result = await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        result.Status.Should().Be(RemittanceLifecycleStatus.Posted);
        result.Replay.Should().BeFalse();
        result.ClaimsPosted.Should().Be(1);
        result.AccumulatorsApplied.Should().Be(1);
        harness.Claims.Posted.Should().ContainSingle(p =>
            p.ClaimId == "CLM-P-1001" && p.PaymentAmount == 320m);
        harness.Accumulators.Applied.Should().ContainSingle(a =>
            a.MemberId == "U7777788888" && a.DeductibleDelta == 50m && a.OopDelta == 80m);
        (await harness.Transmissions.GetByIdAsync(harness.Transmission.TransmissionId))!
            .Status.Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
        (await harness.Acks.ListByTransmissionIdAsync(harness.Transmission.TransmissionId))
            .Single().Status.Should().Be(ClaimAcknowledgmentStatus.Accepted);
        (await harness.Receipts.GetByIdAsync(stored.ReceiptId))!
            .Status.Should().Be(RemittanceLifecycleStatus.Posted);
    }

    [Fact]
    public async Task DuplicatePost_IsReplayWithoutSecondSinkWrite()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");
        var first = await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });
        var second = await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored.ReceiptId,
            TenantId = "tenant-alpha"
        });

        first.Replay.Should().BeFalse();
        second.Replay.Should().BeTrue();
        second.Status.Should().Be(RemittanceLifecycleStatus.Posted);
        harness.Claims.Posted.Should().ContainSingle();
        harness.Accumulators.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task UnmatchedRemittance_IsNotPosted()
    {
        var harness = Create();
        var processor = new RemittanceProcessor(
            harness.Receipts, harness.Transmissions, NullLogger<RemittanceProcessor>.Instance);
        await processor.ProcessAsync(new GatewayRemittance
        {
            RemittanceId = "era-none",
            Gateway = "Stedi",
            ReceivedAt = DateTimeOffset.UtcNow,
            Claims = { new RemittedClaim { PayerClaimControlNumber = "NOPE", PaidAmount = 10 } }
        });
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-none");

        var result = await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        result.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatch);
        stored.Status.Should().Be(RemittanceLifecycleStatus.Unmatched);
        harness.Claims.Posted.Should().BeEmpty();
        harness.Accumulators.Applied.Should().BeEmpty();
        (await harness.Receipts.GetByIdAsync(stored.ReceiptId))!
            .Status.Should().Be(RemittanceLifecycleStatus.Unmatched);
    }

    [Fact]
    public async Task TenantMismatch_DoesNotPost()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");
        var result = await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-beta"
        });
        result.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatch);
        harness.Claims.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task DoesNotInventRemittance()
    {
        var harness = Create();
        var result = await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = "missing",
            TenantId = "tenant-alpha"
        });
        result.ErrorCategory.Should().Be(GatewayErrorCategory.UnableToMatch);
        (await harness.Receipts.ListByTenantAsync("tenant-alpha", 10)).Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimPostRejected_DoesNotMarkPosted()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");
        var poster = new RemittancePoster(
            harness.Receipts,
            harness.Transmissions,
            new StubClaimSink(RemittanceClaimPostOutcome.Rejected),
            harness.Accumulators,
            NullLogger<RemittancePoster>.Instance);

        var result = await poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        result.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceUnavailable);
        result.Status.Should().Be(RemittanceLifecycleStatus.AvailableForPosting);
        (await harness.Receipts.GetByIdAsync(stored.ReceiptId))!
            .Status.Should().Be(RemittanceLifecycleStatus.AvailableForPosting);
        harness.Accumulators.Applied.Should().BeEmpty();
    }

    [Fact]
    public async Task AccumulatorFailed_DoesNotMarkPosted()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");
        var poster = new RemittancePoster(
            harness.Receipts,
            harness.Transmissions,
            harness.Claims,
            new StubAccumulatorSink(RemittanceAccumulatorApplyOutcome.Failed),
            NullLogger<RemittancePoster>.Instance);

        var result = await poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        result.ErrorCategory.Should().Be(GatewayErrorCategory.ServiceUnavailable);
        (await harness.Receipts.GetByIdAsync(stored.ReceiptId))!
            .Status.Should().Be(RemittanceLifecycleStatus.AvailableForPosting);
    }

    [Fact]
    public async Task ClaimNotFound_StillPostsRemittanceAndAccumulators()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");
        var claims = new StubClaimSink(RemittanceClaimPostOutcome.NotFound);
        var poster = new RemittancePoster(
            harness.Receipts,
            harness.Transmissions,
            claims,
            harness.Accumulators,
            NullLogger<RemittancePoster>.Instance);

        var result = await poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        result.Status.Should().Be(RemittanceLifecycleStatus.Posted);
        result.ClaimsPosted.Should().Be(0);
        result.AccumulatorsApplied.Should().Be(1);
        harness.Accumulators.Applied.Should().ContainSingle();
    }

    [Fact]
    public async Task PostedReceipt_LeavesAvailableForPostingEmpty()
    {
        var harness = await SeedPostedPathAsync();
        var stored = await harness.Receipts.GetByIdempotencyKeyAsync("Stedi", "era-1");
        (await harness.Receipts.ListAvailableForPostingAsync("tenant-alpha", 10))
            .Should().ContainSingle(r => r.ReceiptId == stored!.ReceiptId);

        await harness.Poster.PostAsync(new RemittancePostRequest
        {
            ReceiptId = stored!.ReceiptId,
            TenantId = "tenant-alpha"
        });

        (await harness.Receipts.ListAvailableForPostingAsync("tenant-alpha", 10)).Should().BeEmpty();
        var posted = await harness.Receipts.GetByIdAsync(stored.ReceiptId);
        posted!.PostedAtUtc.Should().NotBeNull();
        posted.Outbox.Should().Contain(e => e.EventType == RemittanceMessageTypes.Posted);
    }

    private static Harness Create()
    {
        var transmissions = new InMemoryClaimTransmissionStore();
        var acks = new InMemoryClaimAcknowledgmentStore();
        var receipts = new InMemoryRemittanceStore();
        var claims = new InMemoryClaimRemittancePostingSink();
        var accumulators = new InMemoryRemittanceAccumulatorSink();
        var poster = new RemittancePoster(
            receipts, transmissions, claims, accumulators, NullLogger<RemittancePoster>.Instance);
        return new Harness(poster, transmissions, acks, receipts, claims, accumulators, null!);
    }

    private static async Task<Harness> SeedPostedPathAsync()
    {
        var harness = Create();
        var tx = new ClaimTransmissionRecord
        {
            TenantId = "tenant-alpha",
            ClaimId = "CLM-P-1001",
            GatewayName = "Stedi",
            Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            SubmissionId = "synthetic-sub-001",
            PatientControlNumber = "CLM-P-1001",
            PayerClaimControlNumber = "PAYER-CCN-9",
            ServiceDateFrom = new DateOnly(2026, 1, 15),
            SubmittedAtUtc = DateTimeOffset.UtcNow,
            InquirySource = new ClaimStatusInquirySource
            {
                Subscriber = new GatewayEligibilityPerson { MemberId = "U7777788888" }
            }
        };
        await harness.Transmissions.SaveAsync(tx);
        await new ClaimAcknowledgmentProcessor(
            harness.Acks, harness.Transmissions, NullLogger<ClaimAcknowledgmentProcessor>.Instance)
            .ProcessAsync(new GatewayClaimAcknowledgment
            {
                AcknowledgmentId = "ack-1",
                Gateway = "Stedi",
                TransmissionId = tx.TransmissionId,
                OriginalSubmissionId = tx.SubmissionId,
                Status = ClaimAcknowledgmentStatus.Accepted,
                ClaimControlNumber = "PAYER-CCN-9",
                ReceivedAt = DateTimeOffset.UtcNow
            });
        await new RemittanceProcessor(
            harness.Receipts, harness.Transmissions, NullLogger<RemittanceProcessor>.Instance)
            .ProcessAsync(new GatewayRemittance
            {
                RemittanceId = "era-1",
                Gateway = "Stedi",
                PaymentAmount = 320m,
                PaymentIdentifier = "EFT-TRACE-1",
                PaymentDate = new DateOnly(2026, 1, 20),
                ReceivedAt = DateTimeOffset.UtcNow,
                Claims =
                {
                    new RemittedClaim
                    {
                        PayerClaimControlNumber = "PAYER-CCN-9",
                        PatientControlNumber = "CLM-P-1001",
                        ChargedAmount = 500m,
                        PaidAmount = 320m,
                        PatientResponsibilityAmount = 80m,
                        Adjustments =
                        {
                            new RemittanceAdjustment
                            {
                                GroupCode = "PR", ReasonCode = "1", Amount = 50m,
                                Kind = RemittanceAdjustmentKind.Deductible
                            },
                            new RemittanceAdjustment
                            {
                                GroupCode = "PR", ReasonCode = "2", Amount = 30m,
                                Kind = RemittanceAdjustmentKind.Coinsurance
                            }
                        }
                    }
                }
            });
        return harness with { Transmission = await harness.Transmissions.GetByIdAsync(tx.TransmissionId) ?? tx };
    }

    private sealed record Harness(
        RemittancePoster Poster,
        InMemoryClaimTransmissionStore Transmissions,
        InMemoryClaimAcknowledgmentStore Acks,
        InMemoryRemittanceStore Receipts,
        InMemoryClaimRemittancePostingSink Claims,
        InMemoryRemittanceAccumulatorSink Accumulators,
        ClaimTransmissionRecord Transmission);

    private sealed class StubClaimSink : IClaimRemittancePostingSink
    {
        private readonly RemittanceClaimPostOutcome _outcome;

        public StubClaimSink(RemittanceClaimPostOutcome outcome) => _outcome = outcome;

        public Task<RemittanceClaimPostResult> PostAsync(
            RemittanceClaimPost request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemittanceClaimPostResult(_outcome, _outcome.ToString()));
    }

    private sealed class StubAccumulatorSink : IRemittanceAccumulatorSink
    {
        private readonly RemittanceAccumulatorApplyOutcome _outcome;

        public StubAccumulatorSink(RemittanceAccumulatorApplyOutcome outcome) => _outcome = outcome;

        public Task<RemittanceAccumulatorApplyResult> ApplyAsync(
            RemittanceAccumulatorApply request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RemittanceAccumulatorApplyResult(_outcome, _outcome.ToString()));
    }
}
