namespace BenefitPlanService.Services;

/// <summary>
/// Adjudication-path gate that checks provider integrity via the
/// ProviderVerificationEngine (NPPES + OIG/LEIE + SAM.gov + PECOS).
///
/// Unlike IEnrollmentDecisionGate (which checks state Medicaid enrollment),
/// this gate screens for federal program exclusions and NPI deactivation.
/// A provider could pass enrollment validation but be on the OIG exclusion
/// list — this gate catches that.
///
/// <para>
/// The <c>HttpProviderIntegrityGate</c> implementation reads the cached
/// projection on <c>Provider.IntegrityScore</c> by default
/// (provider-service) and only falls back to the live
/// provider-verification-service when the cached score is null or stale,
/// or when callers explicitly request a fresh score. See
/// <c>docs/architecture/integrity-score-consumption.md</c>.
/// </para>
/// </summary>
public interface IProviderIntegrityGate
{
    /// <summary>
    /// Resolve the integrity result for <paramref name="npi"/>.
    /// </summary>
    /// <param name="npi">Provider NPI.</param>
    /// <param name="tenantId">
    /// Tenant id to forward to provider-service and
    /// provider-verification-service. When null, no tenant header is
    /// forwarded.
    /// </param>
    /// <param name="forceRefresh">
    /// When <c>true</c> the cached-projection short-circuit is bypassed
    /// and the gate calls <c>provider-verification-service</c> directly.
    /// Default <c>false</c>; callers that need fresh-only semantics
    /// (admin investigations, on-demand operator action) opt in.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProviderIntegrityResult> CheckAsync(
        string npi,
        string? tenantId = null,
        bool forceRefresh = false,
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

    /// <summary>
    /// True when the gate could not reach a confident pass/exclude
    /// determination -- either because no data source was reachable, or
    /// because the live verification service itself reported a
    /// <c>Failed</c> or <c>ManualReviewRequired</c> status. Distinct from
    /// <see cref="IsExcluded"/>: an excluded provider is a confirmed
    /// finding; a claim for a provider with this flag set could not be
    /// verified either way and should be held for human review rather than
    /// silently denied as excluded or silently paid. <see cref="Passed"/>
    /// is <c>false</c> whenever this is <c>true</c> -- adjudication never
    /// pays a claim it could not verify.
    /// </summary>
    public bool RequiresManualReview { get; init; }

    /// <summary>CARC code when denied (e.g., "B7" for provider excluded from federal programs).</summary>
    public string? DenialCode { get; init; }

    /// <summary>Human-readable denial reason.</summary>
    public string? DenialReason { get; init; }
}
