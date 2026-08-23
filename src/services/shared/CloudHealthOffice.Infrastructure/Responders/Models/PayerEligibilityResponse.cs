using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Responders.Models;

/// <summary>
/// Vendor-neutral payer-side eligibility response (271-equivalent) produced by
/// <see cref="IEligibilityResponder"/>.
///
/// Capable of being translated later into 271 X12, Stedi JSON, FHIR
/// CoverageEligibilityResponse, or another network format. Adapters own that
/// translation; this type contains no vendor-specific fields.
///
/// Transport success and business outcome are separate:
/// HTTP 200 + <see cref="EligibilityBusinessStatus.SubscriberNotFound"/> is a
/// successful transport with a payer business rejection.
/// </summary>
public sealed class PayerEligibilityResponse
{
    /// <summary>Caller-supplied transaction id, echoed when present.</summary>
    public string? TransactionId { get; set; }

    public string? CorrelationId { get; set; }

    public string? ExternalTransactionId { get; set; }

    /// <summary>Cloud Health Office-assigned transaction id for this response.</summary>
    public string ChoTransactionId { get; set; } = string.Empty;

    public EligibilityTransportStatus TransportStatus { get; set; } = EligibilityTransportStatus.Success;

    public EligibilityBusinessStatus BusinessStatus { get; set; } = EligibilityBusinessStatus.UnableToRespond;

    public PayerEligibilityCoverageStatus CoverageStatus { get; set; } = PayerEligibilityCoverageStatus.Unknown;

    /// <summary>CHO domain rejection code (not an X12 AAA code).</summary>
    public string? RejectionCode { get; set; }

    /// <summary>Non-PHI rejection / informational summary.</summary>
    public string? RejectionMessage { get; set; }

    /// <summary>Tenant the inquiry was routed to. Empty when routing failed.</summary>
    public string? TenantId { get; set; }

    public string? CanonicalPayerId { get; set; }

    public string? PayerName { get; set; }

    public GatewayEligibilityPerson? Subscriber { get; set; }

    public GatewayEligibilityPerson? Patient { get; set; }

    public string? PlanId { get; set; }

    public string? PlanName { get; set; }

    public string? GroupNumber { get; set; }

    public DateOnly? CoverageEffectiveDate { get; set; }

    public DateOnly? CoverageTerminationDate { get; set; }

    public PayerEligibilityNetworkStatus NetworkStatus { get; set; } = PayerEligibilityNetworkStatus.Unknown;

    public string? ProviderNpi { get; set; }

    /// <summary>Non-PHI provider / network note (e.g. provider not on file).</summary>
    public string? ProviderMessage { get; set; }

    /// <summary>Normalized benefit lines for requested, supported service types.</summary>
    public List<GatewayEligibilityBenefit> Benefits { get; set; } = new();

    public PayerEligibilityCostShare? Deductible { get; set; }

    public PayerEligibilityCostShare? OutOfPocket { get; set; }

    /// <summary>Non-PHI informational messages (limitations, unsupported STC, etc.).</summary>
    public List<string> Messages { get; set; } = new();

    public bool IsEligible =>
        TransportStatus == EligibilityTransportStatus.Success &&
        BusinessStatus == EligibilityBusinessStatus.Success &&
        CoverageStatus == PayerEligibilityCoverageStatus.Active;
}

/// <summary>
/// Deductible or out-of-pocket snapshot. Values are read from existing
/// accumulator state; an eligibility inquiry never mutates them.
/// </summary>
public sealed class PayerEligibilityCostShare
{
    public decimal? IndividualAmount { get; set; }

    public decimal? IndividualMet { get; set; }

    public decimal? IndividualRemaining { get; set; }

    public decimal? FamilyAmount { get; set; }

    public decimal? FamilyMet { get; set; }

    public decimal? FamilyRemaining { get; set; }

    public string TimePeriod { get; set; } = "CalendarYear";

    public bool InNetwork { get; set; } = true;
}
