using CloudHealthOffice.Infrastructure.Responders.Directory;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilityReadOnlyTests
{
    [Fact]
    public async Task Inquiry_DoesNotMutateAccumulatorsOrCreateSideEffects()
    {
        var harness = new PayerEligibilityTestHarness();
        var before = await harness.Directory.GetAccumulatorsAsync(
            ChoDemoEligibilitySeed.TenantId,
            ChoDemoEligibilitySeed.SubscriberMemberId,
            ChoDemoEligibilitySeed.PlanId);

        await harness.Responder.RespondAsync(PayerEligibilityTestHarness.SelfInquiry());
        await harness.Responder.RespondAsync(PayerEligibilityTestHarness.DependentInquiry());

        var after = await harness.Directory.GetAccumulatorsAsync(
            ChoDemoEligibilitySeed.TenantId,
            ChoDemoEligibilitySeed.SubscriberMemberId,
            ChoDemoEligibilitySeed.PlanId);

        after.Should().NotBeNull();
        after!.IndividualDeductibleRemaining.Should().Be(before!.IndividualDeductibleRemaining);
        after.IndividualOutOfPocketRemaining.Should().Be(before.IndividualOutOfPocketRemaining);
        after.FamilyDeductibleRemaining.Should().Be(before.FamilyDeductibleRemaining);
        after.FamilyOutOfPocketRemaining.Should().Be(before.FamilyOutOfPocketRemaining);

        harness.Directory.MutationProbe.IsUnchanged.Should().BeTrue();
        harness.Directory.MutationProbe.AccumulatorWrites.Should().Be(0);
        harness.Directory.MutationProbe.ClaimCreates.Should().Be(0);
        harness.Directory.MutationProbe.AuthorizationCreates.Should().Be(0);
        harness.Directory.MutationProbe.PaymentCreates.Should().Be(0);
        harness.Directory.MutationProbe.MemberWrites.Should().Be(0);
        harness.Directory.MutationProbe.CoverageWrites.Should().Be(0);
    }

    [Fact]
    public async Task DirectoryWriteHooks_AreDetectableWhenCalled()
    {
        var directory = new InMemoryPayerEligibilityDirectory();
        directory.RecordAccumulatorWrite(
            ChoDemoEligibilitySeed.TenantId,
            ChoDemoEligibilitySeed.SubscriberMemberId,
            ChoDemoEligibilitySeed.PlanId,
            remainingDeductible: 1m);
        directory.RecordClaimCreate();
        directory.RecordAuthorizationCreate();
        directory.RecordPaymentCreate();
        directory.RecordMemberWrite();
        directory.RecordCoverageWrite();

        directory.MutationProbe.IsUnchanged.Should().BeFalse();
        directory.MutationProbe.AccumulatorWrites.Should().Be(1);

        var snapshot = await directory.GetAccumulatorsAsync(
            ChoDemoEligibilitySeed.TenantId,
            ChoDemoEligibilitySeed.SubscriberMemberId,
            ChoDemoEligibilitySeed.PlanId);
        snapshot!.IndividualDeductibleRemaining.Should().Be(1m);
    }
}
