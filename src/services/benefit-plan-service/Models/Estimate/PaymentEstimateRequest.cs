using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
// Namespace alias for the benefit-engine domain: BenefitPlanService.Models
// also defines a NetworkTier class (plan tier config) which would otherwise
// shadow the engine's NetworkTier enum in this namespace, so we qualify it.
using Engine = CloudHealthOffice.BenefitEngine.Domain;

namespace BenefitPlanService.Models.Estimate;

/// <summary>
/// Provider-facing request for a prospective claim payment estimate.
///
/// <para>
/// This is a deliberately separate wire contract from the internal
/// <c>AdjudicationRequest</c>/<c>BenefitResolutionRequest</c> engine models.
/// A provider application (e.g. CloudDentalOffice) submits a proposed set of
/// services <em>before</em> a real claim exists and asks what the expected
/// payer/patient responsibility would be. Nothing here mutates claim,
/// payment, or accumulator state.
/// </para>
///
/// <para>
/// The tenant is always taken from the authenticated request context
/// (JWT claim or <c>X-Tenant-ID</c> header). Any tenant identifier that
/// might appear in the body is ignored — it can never override the
/// authenticated tenant.
/// </para>
/// </summary>
public record PaymentEstimateRequest
{
    /// <summary>
    /// Caller-supplied correlation id, echoed back on the response. Optional.
    /// Because estimates are read-only, retries with the same id are harmless
    /// and no database state is created for idempotency.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>Member identifier whose coverage the estimate is quoted against.</summary>
    [Required]
    public string MemberId { get; init; } = default!;

    /// <summary>Subscriber identifier (family accumulator owner). Optional; defaults to the member.</summary>
    public string? SubscriberId { get; init; }

    /// <summary>Benefit plan the member is enrolled in.</summary>
    [Required]
    public Guid BenefitPlanId { get; init; }

    /// <summary>Rendering/billing provider NPI.</summary>
    [Required]
    public string ProviderNpi { get; init; } = default!;

    /// <summary>Provider taxonomy code. Used for prior-auth rule evaluation.</summary>
    public string? ProviderTaxonomy { get; init; }

    /// <summary>Proposed date of service.</summary>
    [Required]
    public DateOnly ServiceDate { get; init; }

    /// <summary>
    /// Claim type: "Professional", "Institutional", or "Dental". Defaults to
    /// Professional. The estimate is intentionally not dental-only — the same
    /// endpoint supports professional and institutional claims.
    /// </summary>
    public string ClaimType { get; init; } = "Professional";

    /// <summary>
    /// Line of business name (e.g. "Commercial", "Medicare", "Medicaid",
    /// "CHIP", "Exchange"). Optional; drives operating-mode routing and
    /// LOB-specific rules. Note that dental is a <see cref="ClaimType"/>,
    /// not a line of business.
    /// </summary>
    public string? LineOfBusiness { get; init; }

    /// <summary>Network tier the provider falls in for this member. Defaults to in-network.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Engine.NetworkTier NetworkTier { get; init; } = Engine.NetworkTier.InNetwork;

    /// <summary>State jurisdiction (e.g. "TX"). Used for prior-auth rule resolution.</summary>
    public string? StateCode { get; init; }

    /// <summary>
    /// Prior authorization number already on file, if any. When present, a
    /// prior-auth requirement is treated as satisfied rather than flagged.
    /// </summary>
    public string? PriorAuthorizationNumber { get; init; }

    /// <summary>Proposed service lines to price and adjudicate prospectively.</summary>
    [Required]
    public List<PaymentEstimateLineRequest> Lines { get; init; } = [];
}

/// <summary>
/// One proposed service line on a payment-estimate request.
/// </summary>
public record PaymentEstimateLineRequest
{
    /// <summary>1-based line number, echoed onto the response line.</summary>
    public int LineNumber { get; init; }

    /// <summary>Procedure code (CPT/HCPCS for professional, CDT for dental, etc.).</summary>
    [Required]
    public string ProcedureCode { get; init; } = default!;

    /// <summary>Code system for <see cref="ProcedureCode"/> — "CPT", "HCPCS", "CDT". Defaults to CPT.</summary>
    public string CodeType { get; init; } = "CPT";

    /// <summary>Provider's billed charge for this line.</summary>
    public decimal ChargeAmount { get; init; }

    /// <summary>Units billed. Defaults to 1.</summary>
    public decimal Units { get; init; } = 1;

    /// <summary>Procedure modifiers (e.g. "26", "50").</summary>
    public List<string> Modifiers { get; init; } = [];

    /// <summary>Place of service code (e.g. "11" office, "21" inpatient).</summary>
    public string? PlaceOfService { get; init; }

    /// <summary>Revenue code (institutional claims).</summary>
    public string? RevenueCode { get; init; }

    /// <summary>Diagnosis codes supporting this line.</summary>
    public List<string> DiagnosisCodes { get; init; } = [];

    // ── Dental-specific line detail ──────────────────────────────────────
    // Preserved on the request so future dental adjudication can consume it.
    // Not required for professional/institutional claims.

    /// <summary>Tooth number (dental). Echoed back on the response line.</summary>
    public string? ToothNumber { get; init; }

    /// <summary>Tooth surface (dental), e.g. "MO".</summary>
    public string? ToothSurface { get; init; }

    /// <summary>Oral cavity quadrant (dental).</summary>
    public string? Quadrant { get; init; }
}
