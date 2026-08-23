using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Directory;
using CloudHealthOffice.Infrastructure.Responders.Models;
using CloudHealthOffice.Infrastructure.Responders.Routing;
using CloudHealthOffice.Infrastructure.Tests.Gateways;

namespace CloudHealthOffice.Infrastructure.Tests.Responders;

public class PayerEligibilityResponderTests
{
    private readonly PayerEligibilityTestHarness _harness = new();

    [Fact]
    public async Task ActiveSubscriber_ReturnsActiveCoverageAndBenefits()
    {
        var envelope = await _harness.Responder.RespondAsync(PayerEligibilityTestHarness.SelfInquiry());

        envelope.IsSuccess.Should().BeTrue();
        var result = envelope.Result!;
        result.TransportStatus.Should().Be(EligibilityTransportStatus.Success);
        result.BusinessStatus.Should().Be(EligibilityBusinessStatus.Success);
        result.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
        result.IsEligible.Should().BeTrue();
        result.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
        result.PlanName.Should().Be(ChoDemoEligibilitySeed.PlanName);
        result.CoverageEffectiveDate.Should().Be(ChoDemoEligibilitySeed.ActiveCoverageStart);
        result.CoverageTerminationDate.Should().Be(ChoDemoEligibilitySeed.ActiveCoverageEnd);
        result.Deductible!.IndividualRemaining.Should().Be(ChoDemoEligibilitySeed.IndividualDeductibleRemaining);
        result.OutOfPocket!.IndividualRemaining.Should().Be(ChoDemoEligibilitySeed.IndividualOopRemaining);
        result.Benefits.Should().Contain(b => b.CopayAmount == ChoDemoEligibilitySeed.InNetworkCopay);
        result.Benefits.Should().Contain(b => b.CoinsurancePercent == ChoDemoEligibilitySeed.InNetworkCoinsurance);
        envelope.Metadata.Status.Should().Be(GatewayTransactionStatus.Completed);
        envelope.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.None);
    }

    [Fact]
    public async Task SubscriberNotFound_IsBusinessRejection()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(memberId: "NO-SUCH-MEMBER");
        inquiry.Subscriber!.FirstName = "Nobody";
        inquiry.Subscriber.LastName = "Here";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1999, 9, 9);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.IsSuccess.Should().BeTrue("transport succeeded");
        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.SubscriberNotFound);
        envelope.Result.IsEligible.Should().BeFalse();
        envelope.Result.Subscriber.Should().BeNull("ambiguous/not-found must not leak another member");
        envelope.Metadata.Status.Should().Be(GatewayTransactionStatus.Rejected);
        envelope.Metadata.ErrorCategory.Should().Be(GatewayErrorCategory.PayerRejected);
    }

    [Fact]
    public async Task InvalidSubscriber_WhenIdentityMissing()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry();
        inquiry.Subscriber = new GatewayEligibilityPerson();

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.IsSuccess.Should().BeTrue();
        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.InvalidSubscriber);
    }

    [Fact]
    public async Task AmbiguousSubscriber_DoesNotExposeMember()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(memberId: null);
        inquiry.Subscriber = new GatewayEligibilityPerson
        {
            FirstName = ChoDemoEligibilitySeed.AmbiguousNameFirst,
            LastName = ChoDemoEligibilitySeed.AmbiguousNameLast,
            DateOfBirth = ChoDemoEligibilitySeed.AmbiguousDateOfBirth
        };

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.SubscriberAmbiguous);
        envelope.Result.Subscriber.Should().BeNull();
        envelope.Result.Patient.Should().BeNull();
        envelope.Result.Benefits.Should().BeEmpty();
    }

    [Fact]
    public async Task TenantIsolation_OtherTenantMemberIsNotVisible()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry();
        inquiry.Subscriber = new GatewayEligibilityPerson
        {
            MemberId = ChoDemoEligibilitySeed.OtherTenantMemberId,
            FirstName = "Other",
            LastName = "Person",
            DateOfBirth = new DateOnly(1985, 9, 9)
        };

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.TenantId.Should().Be(ChoDemoEligibilitySeed.TenantId);
        envelope.Result.BusinessStatus.Should().Be(EligibilityBusinessStatus.SubscriberNotFound);
    }

    [Fact]
    public async Task ValidDependent_ReturnsDependentCoverage()
    {
        var envelope = await _harness.Responder.RespondAsync(PayerEligibilityTestHarness.DependentInquiry());

        envelope.Result!.IsEligible.Should().BeTrue();
        envelope.Result.Subscriber!.MemberId.Should().Be(ChoDemoEligibilitySeed.SubscriberMemberId);
        envelope.Result.Patient!.MemberId.Should().Be(ChoDemoEligibilitySeed.DependentMemberId);
        envelope.Result.Patient.RelationshipToSubscriber.Should().Be(GatewayEligibilityPerson.Relationship.Child);
        envelope.Result.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
    }

    [Fact]
    public async Task DependentNotFound()
    {
        var inquiry = PayerEligibilityTestHarness.DependentInquiry();
        inquiry.Patient = new GatewayEligibilityPerson
        {
            FirstName = "Missing",
            LastName = "Child",
            DateOfBirth = new DateOnly(2010, 1, 1),
            RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Child
        };

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.DependentNotFound);
        envelope.Result.Patient.Should().BeNull();
    }

    [Fact]
    public async Task DependentDoesNotBelongToSubscriber()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(memberId: ChoDemoEligibilitySeed.InactiveMemberId);
        inquiry.Subscriber!.FirstName = "Inactive";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1975, 3, 1);
        inquiry.Patient = new GatewayEligibilityPerson
        {
            MemberId = ChoDemoEligibilitySeed.DependentMemberId,
            FirstName = ChoDemoEligibilitySeed.DependentFirstName,
            LastName = ChoDemoEligibilitySeed.DependentLastName,
            DateOfBirth = ChoDemoEligibilitySeed.DependentDateOfBirth,
            RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Child
        };

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.DependentNotFound);
    }

    [Fact]
    public async Task SelfRelationship_DoesNotTreatPatientAsDependent()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry();
        inquiry.Patient = new GatewayEligibilityPerson
        {
            MemberId = ChoDemoEligibilitySeed.SubscriberMemberId,
            FirstName = ChoDemoEligibilitySeed.SubscriberFirstName,
            LastName = ChoDemoEligibilitySeed.SubscriberLastName,
            DateOfBirth = ChoDemoEligibilitySeed.SubscriberDateOfBirth,
            RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
        };

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.IsEligible.Should().BeTrue();
        envelope.Result.Patient!.MemberId.Should().Be(ChoDemoEligibilitySeed.SubscriberMemberId);
    }

    [Fact]
    public async Task InactiveMember_AfterTermination_ReturnsTerminatedNotException()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(
            memberId: ChoDemoEligibilitySeed.InactiveMemberId,
            serviceDate: new DateOnly(2026, 8, 23));
        inquiry.Subscriber!.FirstName = "Inactive";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1975, 3, 1);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.IsSuccess.Should().BeTrue();
        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.Success);
        envelope.Result.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Terminated);
        envelope.Result.IsEligible.Should().BeFalse();
        envelope.Result.Benefits.Should().BeEmpty();
    }

    [Fact]
    public async Task FutureCoverage_ReturnsFuture()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(
            memberId: ChoDemoEligibilitySeed.FutureMemberId,
            serviceDate: new DateOnly(2026, 8, 23));
        inquiry.Subscriber!.FirstName = "Future";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1990, 6, 15);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Future);
        envelope.Result.IsEligible.Should().BeFalse();
    }

    [Fact]
    public async Task TerminatedCoverage_ServiceDateAfterEnd()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(
            memberId: ChoDemoEligibilitySeed.TerminatedMemberId,
            serviceDate: ChoDemoEligibilitySeed.TerminatedCoverageEnd.AddDays(1));
        inquiry.Subscriber!.FirstName = "Terminated";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1970, 1, 1);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Terminated);
    }

    [Fact]
    public async Task CoverageEffectiveDate_IsInclusive()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(
            memberId: ChoDemoEligibilitySeed.TerminatedMemberId,
            serviceDate: ChoDemoEligibilitySeed.TerminatedCoverageStart);
        inquiry.Subscriber!.FirstName = "Terminated";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1970, 1, 1);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
        envelope.Result.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task CoverageTerminationDate_IsInclusive()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(
            memberId: ChoDemoEligibilitySeed.TerminatedMemberId,
            serviceDate: ChoDemoEligibilitySeed.TerminatedCoverageEnd);
        inquiry.Subscriber!.FirstName = "Terminated";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1970, 1, 1);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
    }

    [Fact]
    public async Task ServiceDateBeforeEffective_IsFuture()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry(
            memberId: ChoDemoEligibilitySeed.TerminatedMemberId,
            serviceDate: ChoDemoEligibilitySeed.TerminatedCoverageStart.AddDays(-1));
        inquiry.Subscriber!.FirstName = "Terminated";
        inquiry.Subscriber.LastName = "Member";
        inquiry.Subscriber.DateOfBirth = new DateOnly(1970, 1, 1);

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Future);
    }

    [Fact]
    public async Task UnsupportedServiceType_DoesNotInventBenefits()
    {
        var envelope = await _harness.Responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(serviceType: "98"));

        envelope.IsSuccess.Should().BeTrue();
        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.UnsupportedServiceType);
        envelope.Result.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
        envelope.Result.Benefits.Should().BeEmpty();
        envelope.Result.Messages.Should().Contain(m => m.Contains("service type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InNetworkProvider_ExposesInNetworkBenefits()
    {
        var envelope = await _harness.Responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(npi: ChoDemoEligibilitySeed.InNetworkNpi));

        envelope.Result!.NetworkStatus.Should().Be(PayerEligibilityNetworkStatus.InNetwork);
        envelope.Result.Benefits.Should().OnlyContain(b => b.InNetwork);
        envelope.Result.Benefits.Should().Contain(b => b.CopayAmount == ChoDemoEligibilitySeed.InNetworkCopay);
    }

    [Fact]
    public async Task OutOfNetworkProvider_ExposesOutOfNetworkBenefits()
    {
        var envelope = await _harness.Responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(npi: ChoDemoEligibilitySeed.OutOfNetworkNpi));

        envelope.Result!.NetworkStatus.Should().Be(PayerEligibilityNetworkStatus.OutOfNetwork);
        envelope.Result.IsEligible.Should().BeTrue();
        envelope.Result.Benefits.Should().Contain(b => b.CopayAmount == ChoDemoEligibilitySeed.OutOfNetworkCopay);
        envelope.Result.Benefits.Should().Contain(b => b.CoinsurancePercent == ChoDemoEligibilitySeed.OutOfNetworkCoinsurance);
    }

    [Fact]
    public async Task UnknownProvider_DoesNotRejectRequest()
    {
        var envelope = await _harness.Responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(npi: ChoDemoEligibilitySeed.UnknownNpi));

        envelope.Result!.IsEligible.Should().BeTrue();
        envelope.Result.NetworkStatus.Should().Be(PayerEligibilityNetworkStatus.ProviderNotOnFile);
        envelope.Result.ProviderMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InvalidPayer_IsBusinessRejection()
    {
        var envelope = await _harness.Responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(payerId: "NO-SUCH-PAYER"));

        envelope.IsSuccess.Should().BeTrue();
        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.InvalidPayer);
        envelope.Result.TransportStatus.Should().Be(EligibilityTransportStatus.Success);
    }

    [Fact]
    public async Task AmbiguousPayer_IsBusinessRejection()
    {
        var envelope = await _harness.Responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(payerId: ChoDemoEligibilitySeed.AmbiguousExternalId));

        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.AmbiguousPayer);
        envelope.Result.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task InvalidDate_WhenMissing()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry();
        inquiry.DateOfService = default;

        var envelope = await _harness.Responder.RespondAsync(inquiry);

        envelope.Result!.BusinessStatus.Should().Be(EligibilityBusinessStatus.InvalidDate);
    }

    [Fact]
    public async Task Replay_IsSafeAndDoesNotMutate()
    {
        var inquiry = PayerEligibilityTestHarness.SelfInquiry();
        var first = await _harness.Responder.RespondAsync(inquiry);
        var probe = Snapshot(_harness.Directory.MutationProbe);
        var remaining = first.Result!.Deductible!.IndividualRemaining;

        var second = await _harness.Responder.RespondAsync(inquiry);

        second.Result!.IsEligible.Should().BeTrue();
        second.Result.Deductible!.IndividualRemaining.Should().Be(remaining);
        _harness.Directory.MutationProbe.AccumulatorWrites.Should().Be(probe.AccumulatorWrites);
        _harness.Directory.MutationProbe.IsUnchanged.Should().BeTrue();
    }

    [Fact]
    public async Task CoverageLookup_SelectsPeriodContainingServiceDate()
    {
        var early = new PayerDirectoryCoverage
        {
            TenantId = ChoDemoEligibilitySeed.TenantId,
            CoverageId = "COV-EARLY",
            SubscriberMemberId = ChoDemoEligibilitySeed.SubscriberMemberId,
            MemberId = ChoDemoEligibilitySeed.SubscriberMemberId,
            PlanId = ChoDemoEligibilitySeed.PlanId,
            PlanName = ChoDemoEligibilitySeed.PlanName,
            GroupNumber = ChoDemoEligibilitySeed.GroupNumber,
            EffectiveDate = new DateOnly(2020, 1, 1),
            TerminationDate = new DateOnly(2022, 12, 31)
        };
        var later = new PayerDirectoryCoverage
        {
            TenantId = ChoDemoEligibilitySeed.TenantId,
            CoverageId = "COV-LATER",
            SubscriberMemberId = ChoDemoEligibilitySeed.SubscriberMemberId,
            MemberId = ChoDemoEligibilitySeed.SubscriberMemberId,
            PlanId = ChoDemoEligibilitySeed.PlanId,
            PlanName = ChoDemoEligibilitySeed.PlanName,
            GroupNumber = ChoDemoEligibilitySeed.GroupNumber,
            EffectiveDate = new DateOnly(2023, 1, 1),
            TerminationDate = new DateOnly(2029, 12, 31)
        };
        var directory = new InMemoryPayerEligibilityDirectory(
            ChoDemoEligibilitySeed.Routes,
            ChoDemoEligibilitySeed.Members,
            new[] { early, later },
            ChoDemoEligibilitySeed.Plans,
            ChoDemoEligibilitySeed.Accumulators,
            ChoDemoEligibilitySeed.Providers);
        var responder = new CloudHealthOfficeEligibilityResponder(
            new PayerEligibilityRouter(directory),
            directory,
            new CapturingLogger<CloudHealthOfficeEligibilityResponder>());

        var inEarly = await responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(serviceDate: new DateOnly(2021, 6, 1)));
        inEarly.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
        inEarly.Result.CoverageEffectiveDate.Should().Be(early.EffectiveDate);
        inEarly.Result.CoverageTerminationDate.Should().Be(early.TerminationDate);

        var inLater = await responder.RespondAsync(
            PayerEligibilityTestHarness.SelfInquiry(serviceDate: new DateOnly(2025, 6, 1)));
        inLater.Result!.CoverageStatus.Should().Be(PayerEligibilityCoverageStatus.Active);
        inLater.Result.CoverageEffectiveDate.Should().Be(later.EffectiveDate);
        inLater.Result.CoverageTerminationDate.Should().Be(later.TerminationDate);
    }

    [Fact]
    public async Task CanonicalAdapter_DelegatesToResponder()
    {
        var envelope = await _harness.Adapter.ProcessAsync(PayerEligibilityTestHarness.SelfInquiry());

        envelope.Result!.IsEligible.Should().BeTrue();
        _harness.Adapter.IsImplemented.Should().BeTrue();
    }

    private static PayerEligibilityMutationProbe Snapshot(PayerEligibilityMutationProbe probe) =>
        new()
        {
            AccumulatorWrites = probe.AccumulatorWrites,
            ClaimCreates = probe.ClaimCreates,
            AuthorizationCreates = probe.AuthorizationCreates,
            PaymentCreates = probe.PaymentCreates,
            MemberWrites = probe.MemberWrites,
            CoverageWrites = probe.CoverageWrites
        };
}
