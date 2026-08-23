using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Responders.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Directory;

/// <summary>
/// Read-only CHO member projection used by the payer eligibility responder.
/// Maps to member-service identity; this type is not a second member store.
/// </summary>
public sealed class PayerDirectoryMember
{
    public string TenantId { get; init; } = string.Empty;

    public string MemberId { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;

    public DateOnly DateOfBirth { get; init; }

    /// <summary>Subscriber member id when this person is a dependent.</summary>
    public string? SubscriberMemberId { get; init; }

    public string RelationshipToSubscriber { get; init; } = GatewayEligibilityPerson.Relationship.Self;

    public bool IsSubscriber =>
        string.Equals(RelationshipToSubscriber, GatewayEligibilityPerson.Relationship.Self, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Read-only coverage projection (coverage-service).</summary>
public sealed class PayerDirectoryCoverage
{
    public string TenantId { get; init; } = string.Empty;

    public string CoverageId { get; init; } = string.Empty;

    public string SubscriberMemberId { get; init; } = string.Empty;

    /// <summary>Member this coverage applies to (subscriber or dependent).</summary>
    public string MemberId { get; init; } = string.Empty;

    public string PlanId { get; init; } = string.Empty;

    public string PlanName { get; init; } = string.Empty;

    public string? GroupNumber { get; init; }

    public DateOnly EffectiveDate { get; init; }

    public DateOnly? TerminationDate { get; init; }

    public PayerEligibilityCoverageStatus Evaluate(DateOnly serviceDate)
    {
        if (serviceDate < EffectiveDate)
        {
            return PayerEligibilityCoverageStatus.Future;
        }

        if (TerminationDate is { } end && serviceDate > end)
        {
            return PayerEligibilityCoverageStatus.Terminated;
        }

        return PayerEligibilityCoverageStatus.Active;
    }
}

/// <summary>Read-only plan / benefit projection (benefit-plan-service / benefit engine).</summary>
public sealed class PayerDirectoryPlan
{
    public string TenantId { get; init; } = string.Empty;

    public string PlanId { get; init; } = string.Empty;

    public string PlanName { get; init; } = string.Empty;

    /// <summary>Service type codes this plan can answer. Others are unsupported.</summary>
    public IReadOnlyCollection<string> SupportedServiceTypeCodes { get; init; } =
        new[] { Models.ServiceTypeCode.HealthBenefitPlanCoverage };

    public IReadOnlyList<PayerDirectoryBenefit> Benefits { get; init; } = Array.Empty<PayerDirectoryBenefit>();
}

/// <summary>A single configured benefit line for a plan and network status.</summary>
public sealed class PayerDirectoryBenefit
{
    public string ServiceTypeCode { get; init; } = Models.ServiceTypeCode.HealthBenefitPlanCoverage;

    public string ServiceTypeName { get; init; } = "Health Benefit Plan Coverage";

    public string BenefitCode { get; init; } = "1";

    public string? CoverageLevel { get; init; } = "IND";

    public bool InNetwork { get; init; } = true;

    public string? TimePeriod { get; init; }

    public decimal? Amount { get; init; }

    public decimal? Percent { get; init; }

    public decimal? CopayAmount { get; init; }

    public decimal? CoinsurancePercent { get; init; }

    public bool? AuthorizationRequired { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Read-only accumulator snapshot (accumulator-service). The responder never
/// writes these values.
/// </summary>
public sealed class PayerDirectoryAccumulatorSnapshot
{
    public string TenantId { get; init; } = string.Empty;

    public string MemberId { get; init; } = string.Empty;

    public string PlanId { get; init; } = string.Empty;

    public decimal IndividualDeductible { get; init; }

    public decimal IndividualDeductibleMet { get; init; }

    public decimal IndividualDeductibleRemaining { get; init; }

    public decimal FamilyDeductible { get; init; }

    public decimal FamilyDeductibleMet { get; init; }

    public decimal FamilyDeductibleRemaining { get; init; }

    public decimal IndividualOutOfPocketMax { get; init; }

    public decimal IndividualOutOfPocketMet { get; init; }

    public decimal IndividualOutOfPocketRemaining { get; init; }

    public decimal FamilyOutOfPocketMax { get; init; }

    public decimal FamilyOutOfPocketMet { get; init; }

    public decimal FamilyOutOfPocketRemaining { get; init; }

    public PayerEligibilityCostShare ToDeductible(bool inNetwork) => new()
    {
        IndividualAmount = IndividualDeductible,
        IndividualMet = IndividualDeductibleMet,
        IndividualRemaining = IndividualDeductibleRemaining,
        FamilyAmount = FamilyDeductible,
        FamilyMet = FamilyDeductibleMet,
        FamilyRemaining = FamilyDeductibleRemaining,
        TimePeriod = "CalendarYear",
        InNetwork = inNetwork
    };

    public PayerEligibilityCostShare ToOutOfPocket(bool inNetwork) => new()
    {
        IndividualAmount = IndividualOutOfPocketMax,
        IndividualMet = IndividualOutOfPocketMet,
        IndividualRemaining = IndividualOutOfPocketRemaining,
        FamilyAmount = FamilyOutOfPocketMax,
        FamilyMet = FamilyOutOfPocketMet,
        FamilyRemaining = FamilyOutOfPocketRemaining,
        TimePeriod = "CalendarYear",
        InNetwork = inNetwork
    };
}

/// <summary>Read-only provider / network projection (provider-service).</summary>
public sealed class PayerDirectoryProvider
{
    public string TenantId { get; init; } = string.Empty;

    public string Npi { get; init; } = string.Empty;

    public string? OrganizationName { get; init; }

    public bool InNetwork { get; init; }
}

/// <summary>Trusted inbound route from an external payer identity to a CHO tenant.</summary>
public sealed class PayerEligibilityRoute
{
    /// <summary>External identifier as presented by the network (payer id, trading partner id, or endpoint id).</summary>
    public string ExternalIdentifier { get; init; } = string.Empty;

    /// <summary>Kind of identifier: payer-id, trading-partner-id, or endpoint-id.</summary>
    public string IdentifierKind { get; init; } = IdentifierKinds.PayerId;

    public string TenantId { get; init; } = string.Empty;

    public string CanonicalPayerId { get; init; } = string.Empty;

    public string PayerName { get; init; } = string.Empty;

    public static class IdentifierKinds
    {
        public const string PayerId = "payer-id";
        public const string TradingPartnerId = "trading-partner-id";
        public const string EndpointId = "endpoint-id";
    }
}

/// <summary>Result of an exact member lookup. Never includes other members on Ambiguous.</summary>
public sealed class MemberLookupResult
{
    public MemberLookupStatus Status { get; init; }

    public PayerDirectoryMember? Member { get; init; }

    public static MemberLookupResult Matched(PayerDirectoryMember member) =>
        new() { Status = MemberLookupStatus.Matched, Member = member };

    public static MemberLookupResult NotFound() =>
        new() { Status = MemberLookupStatus.NotFound };

    public static MemberLookupResult Ambiguous() =>
        new() { Status = MemberLookupStatus.Ambiguous };

    public static MemberLookupResult Invalid() =>
        new() { Status = MemberLookupStatus.InvalidRequest };
}

/// <summary>
/// Counters proving a 270/271 inquiry did not mutate CHO business state.
/// Incremented only by explicit write methods on the directory.
/// </summary>
public sealed class PayerEligibilityMutationProbe
{
    public int AccumulatorWrites { get; set; }

    public int ClaimCreates { get; set; }

    public int AuthorizationCreates { get; set; }

    public int PaymentCreates { get; set; }

    public int MemberWrites { get; set; }

    public int CoverageWrites { get; set; }

    public bool IsUnchanged =>
        AccumulatorWrites == 0 &&
        ClaimCreates == 0 &&
        AuthorizationCreates == 0 &&
        PaymentCreates == 0 &&
        MemberWrites == 0 &&
        CoverageWrites == 0;
}
