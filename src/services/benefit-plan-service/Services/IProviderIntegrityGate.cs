namespace BenefitPlanService.Services;

/// <summary>
/// Adjudication-path gate that checks provider integrity via the
/// ProviderVerificationEngine (NPPES + OIG/LEIE + SAM.gov + PECOS).
///
/// Unlike IEnrollmentDecisionGate (which checks state Medicaid enrollment),
/// this gate screens for federal program exclusions and NPI deactivation.
/// A provider could pass enrollment validation but be on the OIG exclusion
/// list — this gate catches that.
/// </summary>
public interface IProviderIntegrityGate
{
    Task<ProviderIntegrityResult> CheckAsync(
        string npi,
        CancellationToken ct = default);
}

public record ProviderIntegrityResult
{
    public bool Passed { get; init; }

    /// <summary>Provider composite integrity score (0-100). Null when service is unavailable.</summary>
    public int? IntegrityScore { get; init; }

    /// <summary>Integrity rating: Clear, Advisory, Caution, Alert, Blocked.</summary>
    public string? Rating { get; init; }

    /// <summary>True when the provider appears on OIG/LEIE or SAM.gov exclusion lists.</summary>
    public bool IsExcluded { get; init; }

    /// <summary>CARC code when denied (e.g., "B7" for provider excluded from federal programs).</summary>
    public string? DenialCode { get; init; }

    /// <summary>Human-readable denial reason.</summary>
    public string? DenialReason { get; init; }
}
