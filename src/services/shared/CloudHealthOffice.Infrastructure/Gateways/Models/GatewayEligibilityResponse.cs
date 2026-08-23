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
/// 271 EB segment — it is not a benefit engine. Coverage interpretation stays
/// in Cloud Health Office's benefit services.
/// </summary>
public sealed class GatewayEligibilityBenefit
{
    /// <summary>X12 service type code (EB01), e.g. "30", "33".</summary>
    public string ServiceTypeCode { get; set; } = string.Empty;

    /// <summary>Human-readable service type name.</summary>
    public string ServiceTypeName { get; set; } = string.Empty;

    /// <summary>Coverage level (EB03), e.g. "IND", "FAM".</summary>
    public string? CoverageLevel { get; set; }

    /// <summary>True when the benefit is in-network.</summary>
    public bool InNetwork { get; set; } = true;

    /// <summary>Fixed copay amount, when applicable.</summary>
    public decimal? CopayAmount { get; set; }

    /// <summary>Coinsurance as a fraction (0.20 = 20%), when applicable.</summary>
    public decimal? CoinsurancePercent { get; set; }
}
