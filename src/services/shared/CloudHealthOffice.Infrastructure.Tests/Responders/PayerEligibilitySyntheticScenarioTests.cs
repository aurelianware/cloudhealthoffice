using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilitySyntheticScenarioTests
{
    [Fact]
    public async Task ChoDemoHealthPlan_DependentInquiry_ReturnsActiveBenefits()
    {
        var harness = new PayerEligibilityTestHarness();
        var before = await harness.Directory.GetAccumulatorsAsync(
            ChoDemoEligibilitySeed.TenantId,
            ChoDemoEligibilitySeed.DependentMemberId,
            ChoDemoEligibilitySeed.PlanId);
        var clock = Stopwatch.StartNew();

        var envelope = await harness.Responder.RespondAsync(PayerEligibilityTestHarness.DependentInquiry());
        clock.Stop();

        envelope.IsSuccess.Should().BeTrue();
        var result = envelope.Result!;

        result.PayerName.Should().Be("CHO Demo Health Plan");
        result.CanonicalPayerId.Should().Be(ChoDemoEligibilitySeed.CanonicalPayerId);
        result.Subscriber!.MemberId.Should().Be("MEMBER-10001");
        result.Subscriber.FirstName.Should().Be("John");
        result.Subscriber.LastName.Should().Be("Doe");
        result.Patient!.FirstName.Should().Be("Jane");
        result.Patient.LastName.Should().Be("Doe");
        result.PlanName.Should().Be("Demo PPO");
        result.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
        result.NetworkStatus.Should().Be(PayerEligibilityNetworkStatus.InNetwork);
        result.ProviderNpi.Should().Be("1999999984");
        result.Deductible!.IndividualAmount.Should().Be(1500m);
        result.Deductible.IndividualRemaining.Should().Be(800m);
        result.OutOfPocket!.IndividualAmount.Should().Be(5000m);
        result.OutOfPocket.IndividualRemaining.Should().Be(3200m);
        result.Benefits.Should().Contain(b => b.CopayAmount == 25m);
        result.Benefits.Should().Contain(b => b.CoinsurancePercent == 0.20m);
        result.IsEligible.Should().BeTrue();

        var after = await harness.Directory.GetAccumulatorsAsync(
            ChoDemoEligibilitySeed.TenantId,
            ChoDemoEligibilitySeed.DependentMemberId,
            ChoDemoEligibilitySeed.PlanId);
        after!.IndividualDeductibleRemaining.Should().Be(before!.IndividualDeductibleRemaining);
        harness.Directory.MutationProbe.IsUnchanged.Should().BeTrue();

        envelope.Metadata.Latency.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        clock.ElapsedMilliseconds.Should().BeLessThan(5_000);
    }
}
