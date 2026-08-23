namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral Cloud Health Office eligibility response produced by an
/// <see cref="Capabilities.IEligibilityGateway"/> after normalizing a payer's
/// 271 (or vendor-specific) response.
///
/// This is a Cloud Health Office canonical model. Adapters translate vendor
/// output into this shape; the raw vendor/X12 payload does not travel with it.
/// Business interpretation of these values (benefit application, accumulator
/// math, network determination) remains the responsibility of Cloud Health
/// Office domain services, not the gateway.
/// </summary>
public sealed class GatewayEligibilityResponse
{
    /// <summary>Whether the member has active coverage for the inquiry.</summary>
    public bool IsEligible { get; set; }

    /// <summary>Normalized coverage status.</summary>
    public GatewayCoverageStatus CoverageStatus { get; set; } = GatewayCoverageStatus.Unknown;

    /// <summary>
    /// X12 EB01-style status code carried through for services that need the
    /// raw value (e.g. "1" active, "6" inactive). Non-PHI.
    /// </summary>
    public string StatusCode { get; set; } = string.Empty;

    /// <summary>Non-PHI reason when coverage is not active.</summary>
    public string? RejectionReason { get; set; }

    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? GroupNumber { get; set; }
    public DateOnly? CoverageStart { get; set; }
    public DateOnly? CoverageEnd { get; set; }

    /// <summary>Normalized benefit lines returned by the payer.</summary>
    public List<GatewayEligibilityBenefit> Benefits { get; set; } = new();

    /// <summary>Payer-returned subscriber (policyholder), when present.</summary>
    public GatewayEligibilityPerson? Subscriber { get; set; }

    /// <summary>
    /// Payer-returned patient / dependent, when the 271 distinguishes them
    /// from the subscriber. Null when the payer did not return a dependent.
    /// </summary>
    public GatewayEligibilityPerson? Patient { get; set; }
}

/// <summary>Normalized coverage status independent of any vendor coding.</summary>
public enum GatewayCoverageStatus
{
    Unknown,
    Active,
    Inactive
}

/// <summary>
/// A single normalized benefit line. This is a transport-level projection of a
/// 271 EB (eligibility/benefit) loop — it is not a benefit engine. Coverage
/// interpretation stays in Cloud Health Office's benefit services.
///
/// The fields are named after vendor-neutral X12 271 concepts, never after any
/// vendor. A vendor adapter projects one payer benefit entry onto one instance
/// of this type, preserving what the payer returned without inventing values.
/// </summary>
public sealed class GatewayEligibilityBenefit
{
    /// <summary>
    /// Benefit type code (EB01), e.g. "1" active coverage, "C" deductible,
    /// "A" co-insurance, "B" co-payment, "G" out-of-pocket. Distinguishes the
    /// kind of benefit this line describes.
    /// </summary>
    public string? BenefitCode { get; set; }

    /// <summary>X12 service type code (EB03), e.g. "30", "33".</summary>
    public string ServiceTypeCode { get; set; } = string.Empty;

    /// <summary>Human-readable service type / benefit name.</summary>
    public string ServiceTypeName { get; set; } = string.Empty;

    /// <summary>Coverage level (EB02), e.g. "IND", "FAM".</summary>
    public string? CoverageLevel { get; set; }

    /// <summary>True when the benefit is in-network.</summary>
    public bool InNetwork { get; set; } = true;

    /// <summary>
    /// Time-period qualifier (EB06), e.g. "Calendar Year", "Remaining",
    /// "Visit". Non-PHI. Preserved verbatim from the payer where available.
    /// </summary>
    public string? TimePeriod { get; set; }

    /// <summary>
    /// Monetary amount for this benefit line (EB07). Used generically for
    /// deductible, out-of-pocket, copay, and remaining amounts depending on
    /// <see cref="BenefitCode"/> and <see cref="TimePeriod"/>.
    /// </summary>
    public decimal? Amount { get; set; }

    /// <summary>Percentage for this benefit line (EB08) as a fraction (0.20 = 20%).</summary>
    public decimal? Percent { get; set; }

    /// <summary>Quantity (EB10), e.g. remaining visits.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>Fixed copay amount, when this line describes a co-payment.</summary>
    public decimal? CopayAmount { get; set; }

    /// <summary>Coinsurance as a fraction (0.20 = 20%), when this line describes co-insurance.</summary>
    public decimal? CoinsurancePercent { get; set; }

    /// <summary>Whether prior authorization/certification is required (EB11: Y/N/U).</summary>
    public bool? AuthorizationRequired { get; set; }

    /// <summary>
    /// Free-form informational messages attached to this benefit line. Payers
    /// use these for coverage notes/limitations. Adapters must only place
    /// non-PHI benefit text here.
    /// </summary>
    public List<string> Messages { get; set; } = new();
}
