using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Mock;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

public class MockClaimSubmissionTests
{
    [Fact]
    public async Task ProfessionalClaim_IsAcceptedWithoutMutatingAdjudicationSemantics()
    {
        var gateway = new MockHealthcareGateway(NullLogger<MockHealthcareGateway>.Instance);
        var result = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        result.IsSuccess.Should().BeTrue();
        result.Result!.AcceptedForProcessing.Should().BeTrue();
        result.Result.TransmissionStatus.Should().Be(GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway);
        result.Result.ReplayOfExistingTransmission.Should().BeFalse();
        result.Metadata.TransactionType.Should().Be(HealthcareTransactionType.ProfessionalClaim837P);
        result.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
    }

    [Fact]
    public async Task RepeatedSubmission_ReturnsExistingTransmission()
    {
        var store = new InMemoryClaimTransmissionStore();
        var gateway = new MockHealthcareGateway(
            NullLogger<MockHealthcareGateway>.Instance, timeProvider: null, transmissions: store);
        // The internal ctor with roster is needed for store injection — use public ctor + same store via DI-like:
        gateway = Create(store);

        var first = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var second = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());

        second.Result!.ReplayOfExistingTransmission.Should().BeTrue();
        second.Result.TransmissionId.Should().Be(first.Result!.TransmissionId);
        second.Result.SubmissionId.Should().Be(first.Result.SubmissionId);
    }

    [Fact]
    public async Task ReplacementFrequency_IsADistinctSubmission()
    {
        var store = new InMemoryClaimTransmissionStore();
        var gateway = Create(store);

        var original = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional(frequency: "1"));
        var replacement = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional(frequency: "7"));

        replacement.Result!.ReplayOfExistingTransmission.Should().BeFalse();
        replacement.Result.TransmissionId.Should().NotBe(original.Result!.TransmissionId);
    }

    [Fact]
    public async Task SubmissionAfter277CA_DoesNotResendSameIdempotencyKey()
    {
        var store = new InMemoryClaimTransmissionStore();
        var gateway = Create(store);
        var submitted = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        var tx = await store.GetByIdAsync(submitted.Result!.TransmissionId);
        tx!.Status = GatewayClaimTransmissionStatus.AcknowledgmentAccepted;
        await store.SaveAsync(tx);

        var replay = await gateway.SubmitClaimAsync(GatewayClaimFixtures.Professional());
        replay.Result!.ReplayOfExistingTransmission.Should().BeTrue();
        replay.Result.TransmissionId.Should().Be(submitted.Result.TransmissionId);
        (await store.GetByIdAsync(tx.TransmissionId))!.Status
            .Should().Be(GatewayClaimTransmissionStatus.AcknowledgmentAccepted);
    }

    [Fact]
    public async Task UnbalancedTotals_AreRejectedBeforeTransmission()
    {
        var request = GatewayClaimFixtures.Professional();
        request.TotalCharge = 1.00m;

        var result = await new MockHealthcareGateway(NullLogger<MockHealthcareGateway>.Instance)
            .SubmitClaimAsync(request);

        result.IsSuccess.Should().BeFalse();
        result.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.Validation);
    }

    [Fact]
    public async Task InstitutionalWithoutTypeOfBill_Fails()
    {
        var request = GatewayClaimFixtures.Institutional();
        request.TypeOfBill = null;

        var result = await new MockHealthcareGateway(NullLogger<MockHealthcareGateway>.Instance)
            .SubmitClaimAsync(request);

        result.IsSuccess.Should().BeFalse();
    }

    private static MockHealthcareGateway Create(IClaimTransmissionStore store) =>
        new(NullLogger<MockHealthcareGateway>.Instance, timeProvider: null, transmissions: store);
}
