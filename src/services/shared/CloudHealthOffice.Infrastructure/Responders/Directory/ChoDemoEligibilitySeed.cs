using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// Deterministic synthetic payer-side directory used by development hosts and
/// tests. All names, identifiers, and NPIs are invented; no real member data.
/// </summary>
public static class ChoDemoEligibilitySeed
{
    public const string TenantId = "cho-demo";
    public const string OtherTenantId = "cho-other";

    public const string CanonicalPayerId = "CHO-DEMO-HEALTH";
    public const string PayerName = "CHO Demo Health Plan";
    public const string ExternalPayerId = "CHODEMO";
    public const string TradingPartnerId = "19999";
    public const string AuthenticatedEndpointId = "cho-demo-endpoint";

    public const string AmbiguousExternalId = "CHO-AMBIGUOUS";

    public const string PlanId = "DEMO-PPO";
    public const string PlanName = "Demo PPO";
    public const string GroupNumber = "GRP-DEMO-001";

    public const string SubscriberMemberId = "MEMBER-10001";
    public const string SubscriberFirstName = "John";
    public const string SubscriberLastName = "Doe";
    public static readonly DateOnly SubscriberDateOfBirth = new(1980, 1, 15);

    public const string DependentMemberId = "DEP-10001";
    public const string DependentFirstName = "Jane";
    public const string DependentLastName = "Doe";
    public static readonly DateOnly DependentDateOfBirth = new(2012, 5, 20);

    public const string InactiveMemberId = "MEMBER-INACTIVE";
    public const string FutureMemberId = "MEMBER-FUTURE";
    public const string TerminatedMemberId = "MEMBER-TERMINATED";

    public const string AmbiguousNameFirst = "Alex";
    public const string AmbiguousNameLast = "Ambiguous";
    public static readonly DateOnly AmbiguousDateOfBirth = new(1991, 4, 4);
    public const string AmbiguousMemberIdA = "AMB-1";
    public const string AmbiguousMemberIdB = "AMB-2";

    public const string OtherTenantMemberId = "OTHER-10001";

    public const string InNetworkNpi = "1999999984";
    public const string OutOfNetworkNpi = "1111111112";
    public const string UnknownNpi = "1234567893";

    public static readonly DateOnly ActiveCoverageStart = new(2020, 1, 1);
    public static readonly DateOnly ActiveCoverageEnd = new(2099, 12, 31);
    public static readonly DateOnly InactiveCoverageStart = new(2018, 1, 1);
    public static readonly DateOnly InactiveCoverageEnd = new(2023, 12, 31);
    public static readonly DateOnly FutureCoverageStart = new(2090, 1, 1);
    public static readonly DateOnly FutureCoverageEnd = new(2099, 12, 31);
    public static readonly DateOnly TerminatedCoverageStart = new(2024, 1, 1);
    public static readonly DateOnly TerminatedCoverageEnd = new(2025, 6, 30);

    public const decimal IndividualDeductible = 1500m;
    public const decimal IndividualDeductibleMet = 700m;
    public const decimal IndividualDeductibleRemaining = 800m;
    public const decimal FamilyDeductible = 3000m;
    public const decimal FamilyDeductibleMet = 900m;
    public const decimal FamilyDeductibleRemaining = 2100m;
    public const decimal IndividualOopMax = 5000m;
    public const decimal IndividualOopMet = 1800m;
    public const decimal IndividualOopRemaining = 3200m;
    public const decimal FamilyOopMax = 10000m;
    public const decimal FamilyOopMet = 2500m;
    public const decimal FamilyOopRemaining = 7500m;
    public const decimal InNetworkCopay = 25m;
    public const decimal OutOfNetworkCopay = 50m;
    public const decimal InNetworkCoinsurance = 0.20m;
    public const decimal OutOfNetworkCoinsurance = 0.40m;

    public static IReadOnlyList<PayerEligibilityRoute> Routes { get; } = new[]
    {
        new PayerEligibilityRoute
        {
            ExternalIdentifier = ExternalPayerId,
            IdentifierKind = PayerEligibilityRoute.IdentifierKinds.PayerId,
            TenantId = TenantId,
            CanonicalPayerId = CanonicalPayerId,
            PayerName = PayerName
        },
        new PayerEligibilityRoute
        {
            ExternalIdentifier = TradingPartnerId,
            IdentifierKind = PayerEligibilityRoute.IdentifierKinds.TradingPartnerId,
            TenantId = TenantId,
            CanonicalPayerId = CanonicalPayerId,
            PayerName = PayerName
        },
        new PayerEligibilityRoute
        {
            ExternalIdentifier = CanonicalPayerId,
            IdentifierKind = PayerEligibilityRoute.IdentifierKinds.PayerId,
            TenantId = TenantId,
            CanonicalPayerId = CanonicalPayerId,
            PayerName = PayerName
        },
        new PayerEligibilityRoute
        {
            ExternalIdentifier = AuthenticatedEndpointId,
            IdentifierKind = PayerEligibilityRoute.IdentifierKinds.EndpointId,
            TenantId = TenantId,
            CanonicalPayerId = CanonicalPayerId,
            PayerName = PayerName
        },
        new PayerEligibilityRoute
        {
            ExternalIdentifier = AmbiguousExternalId,
            IdentifierKind = PayerEligibilityRoute.IdentifierKinds.PayerId,
            TenantId = TenantId,
            CanonicalPayerId = CanonicalPayerId,
            PayerName = PayerName
        },
        new PayerEligibilityRoute
        {
            ExternalIdentifier = AmbiguousExternalId,
            IdentifierKind = PayerEligibilityRoute.IdentifierKinds.PayerId,
            TenantId = OtherTenantId,
            CanonicalPayerId = "CHO-OTHER-HEALTH",
            PayerName = "CHO Other Health Plan"
        }
    };

    public static IReadOnlyList<PayerDirectoryMember> Members { get; } = new[]
    {
        Subscriber(TenantId, SubscriberMemberId, SubscriberFirstName, SubscriberLastName, SubscriberDateOfBirth),
        Dependent(TenantId, DependentMemberId, DependentFirstName, DependentLastName, DependentDateOfBirth, SubscriberMemberId),
        Subscriber(TenantId, InactiveMemberId, "Inactive", "Member", new DateOnly(1975, 3, 1)),
        Subscriber(TenantId, FutureMemberId, "Future", "Member", new DateOnly(1990, 6, 15)),
        Subscriber(TenantId, TerminatedMemberId, "Terminated", "Member", new DateOnly(1970, 1, 1)),
        Subscriber(TenantId, AmbiguousMemberIdA, AmbiguousNameFirst, AmbiguousNameLast, AmbiguousDateOfBirth),
        Subscriber(TenantId, AmbiguousMemberIdB, AmbiguousNameFirst, AmbiguousNameLast, AmbiguousDateOfBirth),
        Subscriber(OtherTenantId, OtherTenantMemberId, "Other", "Person", new DateOnly(1985, 9, 9))
    };

    public static IReadOnlyList<PayerDirectoryCoverage> Coverages { get; } = new[]
    {
        Coverage(TenantId, "COV-ACTIVE-SUB", SubscriberMemberId, SubscriberMemberId, ActiveCoverageStart, ActiveCoverageEnd),
        Coverage(TenantId, "COV-ACTIVE-DEP", SubscriberMemberId, DependentMemberId, ActiveCoverageStart, ActiveCoverageEnd),
        Coverage(TenantId, "COV-INACTIVE", InactiveMemberId, InactiveMemberId, InactiveCoverageStart, InactiveCoverageEnd),
        Coverage(TenantId, "COV-FUTURE", FutureMemberId, FutureMemberId, FutureCoverageStart, FutureCoverageEnd),
        Coverage(TenantId, "COV-TERMINATED", TerminatedMemberId, TerminatedMemberId, TerminatedCoverageStart, TerminatedCoverageEnd),
        Coverage(TenantId, "COV-AMB-A", AmbiguousMemberIdA, AmbiguousMemberIdA, ActiveCoverageStart, ActiveCoverageEnd),
        Coverage(TenantId, "COV-AMB-B", AmbiguousMemberIdB, AmbiguousMemberIdB, ActiveCoverageStart, ActiveCoverageEnd),
        Coverage(OtherTenantId, "COV-OTHER", OtherTenantMemberId, OtherTenantMemberId, ActiveCoverageStart, ActiveCoverageEnd)
    };

    public static IReadOnlyList<PayerDirectoryPlan> Plans { get; } = new[]
    {
        new PayerDirectoryPlan
        {
            TenantId = TenantId,
            PlanId = PlanId,
            PlanName = PlanName,
            SupportedServiceTypeCodes = new[] { ServiceTypeCode.HealthBenefitPlanCoverage },
            Benefits = new[]
            {
                Benefit("1", "Active Coverage", inNetwork: true, amount: null, copay: null, coinsurance: null),
                Benefit("C", "Deductible", inNetwork: true, amount: IndividualDeductible, timePeriod: "Calendar Year"),
                Benefit("C", "Remaining Deductible", inNetwork: true, amount: IndividualDeductibleRemaining, timePeriod: "Remaining"),
                Benefit("B", "Co-Payment", inNetwork: true, copay: InNetworkCopay),
                Benefit("A", "Co-Insurance", inNetwork: true, coinsurance: InNetworkCoinsurance),
                Benefit("G", "Out of Pocket (Stop Loss)", inNetwork: true, amount: IndividualOopMax, timePeriod: "Calendar Year"),
                Benefit("G", "Remaining Out of Pocket", inNetwork: true, amount: IndividualOopRemaining, timePeriod: "Remaining"),
                Benefit("C", "Deductible", inNetwork: false, amount: IndividualDeductible, timePeriod: "Calendar Year"),
                Benefit("B", "Co-Payment", inNetwork: false, copay: OutOfNetworkCopay),
                Benefit("A", "Co-Insurance", inNetwork: false, coinsurance: OutOfNetworkCoinsurance)
            }
        },
        new PayerDirectoryPlan
        {
            TenantId = OtherTenantId,
            PlanId = PlanId,
            PlanName = PlanName,
            SupportedServiceTypeCodes = new[] { ServiceTypeCode.HealthBenefitPlanCoverage },
            Benefits = new[]
            {
                Benefit("1", "Active Coverage", inNetwork: true, amount: null, copay: null, coinsurance: null)
            }
        }
    };

    public static IReadOnlyList<PayerDirectoryAccumulatorSnapshot> Accumulators { get; } = new[]
    {
        Accumulator(TenantId, SubscriberMemberId),
        Accumulator(TenantId, DependentMemberId),
        Accumulator(TenantId, InactiveMemberId),
        Accumulator(TenantId, FutureMemberId),
        Accumulator(TenantId, TerminatedMemberId),
        Accumulator(TenantId, AmbiguousMemberIdA),
        Accumulator(TenantId, AmbiguousMemberIdB),
        Accumulator(OtherTenantId, OtherTenantMemberId)
    };

    public static IReadOnlyList<PayerDirectoryProvider> Providers { get; } = new[]
    {
        new PayerDirectoryProvider
        {
            TenantId = TenantId,
            Npi = InNetworkNpi,
            OrganizationName = "ACME Health Services",
            InNetwork = true
        },
        new PayerDirectoryProvider
        {
            TenantId = TenantId,
            Npi = OutOfNetworkNpi,
            OrganizationName = "Out of Network Clinic",
            InNetwork = false
        }
    };

    private static PayerDirectoryMember Subscriber(
        string tenantId, string memberId, string first, string last, DateOnly dob) =>
        new()
        {
            TenantId = tenantId,
            MemberId = memberId,
            FirstName = first,
            LastName = last,
            DateOfBirth = dob,
            RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Self
        };

    private static PayerDirectoryMember Dependent(
        string tenantId, string memberId, string first, string last, DateOnly dob, string subscriberId) =>
        new()
        {
            TenantId = tenantId,
            MemberId = memberId,
            FirstName = first,
            LastName = last,
            DateOfBirth = dob,
            SubscriberMemberId = subscriberId,
            RelationshipToSubscriber = GatewayEligibilityPerson.Relationship.Child
        };

    private static PayerDirectoryCoverage Coverage(
        string tenantId, string coverageId, string subscriberId, string memberId, DateOnly start, DateOnly end) =>
        new()
        {
            TenantId = tenantId,
            CoverageId = coverageId,
            SubscriberMemberId = subscriberId,
            MemberId = memberId,
            PlanId = PlanId,
            PlanName = PlanName,
            GroupNumber = GroupNumber,
            EffectiveDate = start,
            TerminationDate = end
        };

    private static PayerDirectoryBenefit Benefit(
        string code,
        string name,
        bool inNetwork,
        decimal? amount = null,
        decimal? copay = null,
        decimal? coinsurance = null,
        string? timePeriod = null) =>
        new()
        {
            ServiceTypeCode = ServiceTypeCode.HealthBenefitPlanCoverage,
            ServiceTypeName = name,
            BenefitCode = code,
            CoverageLevel = "IND",
            InNetwork = inNetwork,
            TimePeriod = timePeriod,
            Amount = amount,
            CopayAmount = copay,
            CoinsurancePercent = coinsurance,
            Percent = coinsurance,
            AuthorizationRequired = false
        };

    private static PayerDirectoryAccumulatorSnapshot Accumulator(string tenantId, string memberId) =>
        new()
        {
            TenantId = tenantId,
            MemberId = memberId,
            PlanId = PlanId,
            IndividualDeductible = IndividualDeductible,
            IndividualDeductibleMet = IndividualDeductibleMet,
            IndividualDeductibleRemaining = IndividualDeductibleRemaining,
            FamilyDeductible = FamilyDeductible,
            FamilyDeductibleMet = FamilyDeductibleMet,
            FamilyDeductibleRemaining = FamilyDeductibleRemaining,
            IndividualOutOfPocketMax = IndividualOopMax,
            IndividualOutOfPocketMet = IndividualOopMet,
            IndividualOutOfPocketRemaining = IndividualOopRemaining,
            FamilyOutOfPocketMax = FamilyOopMax,
            FamilyOutOfPocketMet = FamilyOopMet,
            FamilyOutOfPocketRemaining = FamilyOopRemaining
        };
}
