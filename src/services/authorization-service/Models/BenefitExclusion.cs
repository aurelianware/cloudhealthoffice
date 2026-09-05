using System.Text.Json.Serialization;

namespace AuthorizationService.Models;

/// <summary>
/// Coding system a requested drug/service identity is expressed in. Kept small
/// and aligned with the systems the platform already sees on authorization
/// requests (NDC / RxNorm for drugs, HCPCS J-codes / CPT for administered
/// services, and the 278 service-type code). <see cref="Unspecified"/> matches
/// any system, so a plan can exclude a bare code without pinning a system.
/// </summary>
public enum DrugServiceCodeSystem
{
    Unspecified = 0,
    Ndc = 1,
    RxNorm = 2,
    Hcpcs = 3,
    Cpt = 4,

    /// <summary>278 UM03 service-type code (e.g. "88" = Pharmacy).</summary>
    ServiceType = 5,
}

/// <summary>Structured, non-PHI exclusion categories.</summary>
public static class ExclusionCategory
{
    /// <summary>A drug/pharmacy request that is out of CMS-0057-F medical PA scope.</summary>
    public const string PharmacyDrug = "pharmacy_drug";

    /// <summary>A service/drug the applicable benefit plan explicitly does not cover.</summary>
    public const string NonCoveredService = "non_covered_service";
}

/// <summary>
/// Structured reason codes for an exclusion determination. These are coded
/// (not free text) so the decision is explainable and reproducible; they carry
/// no PHI and no payer-specific configuration.
/// </summary>
public static class ExclusionReasonCode
{
    /// <summary>Drug/pharmacy request excluded from the CMS-0057-F medical PA scope.</summary>
    public const string DrugExcludedFromMedicalScope = "EXCL-RX-SCOPE";

    /// <summary>Requested drug/service is explicitly non-covered by the benefit plan.</summary>
    public const string NonCoveredBenefit = "EXCL-NONCOV";
}

/// <summary>
/// One benefit-plan exclusion rule: the plan does not cover this drug/service,
/// so a prior-authorization request for it cannot follow the ordinary
/// approvable path. Bound from configuration (see
/// <c>BenefitExclusionOptions</c>) — a mutable class so the config binder can
/// populate it. Contains only codes/categories/reasons, never member or PHI data.
/// </summary>
public sealed class BenefitExclusion
{
    /// <summary>Coding system of <see cref="Code"/>. Unspecified matches any system.</summary>
    public DrugServiceCodeSystem CodeSystem { get; set; } = DrugServiceCodeSystem.Unspecified;

    /// <summary>The excluded code (NDC, RxNorm, J-code, CPT, or service-type code).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Exclusion category (see <see cref="ExclusionCategory"/>).</summary>
    public string Category { get; set; } = ExclusionCategory.NonCoveredService;

    /// <summary>Structured reason code (see <see cref="ExclusionReasonCode"/>).</summary>
    public string ReasonCode { get; set; } = ExclusionReasonCode.NonCoveredBenefit;

    /// <summary>Human-readable reason (no PHI).</summary>
    public string? ReasonText { get; set; }
}

/// <summary>
/// Outcome of evaluating a request against the applicable benefit plan's
/// exclusions. Reproducible from the authorization, the resolved plan
/// exclusions, and the requested code.
/// </summary>
public sealed record BenefitExclusionDetermination
{
    public bool IsExcluded { get; init; }

    /// <summary>The exclusion rule that matched, when excluded.</summary>
    public BenefitExclusion? MatchedExclusion { get; init; }

    /// <summary>The requested code that matched (as submitted).</summary>
    public string? RequestedCode { get; init; }

    /// <summary>The normalized form of the matched code (for audit/repro).</summary>
    public string? NormalizedCode { get; init; }

    [JsonIgnore]
    public static BenefitExclusionDetermination NotExcluded { get; } = new() { IsExcluded = false };

    /// <summary>Structured denial reason code for a matched exclusion.</summary>
    [JsonIgnore]
    public string ReasonCode => MatchedExclusion?.ReasonCode ?? string.Empty;

    /// <summary>Denial reason text for a matched exclusion (coded fallback, no PHI).</summary>
    [JsonIgnore]
    public string ReasonText =>
        MatchedExclusion?.ReasonText
        ?? (MatchedExclusion is null
            ? string.Empty
            : $"Requested drug/service {RequestedCode} is excluded by the member's benefit plan "
              + $"({MatchedExclusion.Category}).");
}
