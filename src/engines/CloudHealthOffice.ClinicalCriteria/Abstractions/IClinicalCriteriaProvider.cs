using CloudHealthOffice.ClinicalCriteria.Models;

namespace CloudHealthOffice.ClinicalCriteria.Abstractions;

/// <summary>
/// One source of clinical criteria.
///
/// CloudHealthOffice ships the seam, not the content: InterQual and MCG are
/// licensed, so their connectors live behind this interface and are inert
/// until a deployment supplies a licence and an endpoint. The in-repo
/// <c>cho-local</c> provider keeps the seam exercised and the platform usable
/// without one.
///
/// Implementations are resolved per request by
/// <see cref="IClinicalCriteriaService"/>, which owns the timeout, the logging
/// and the fail-open behaviour. An implementation may therefore throw: the
/// service turns a failure into an inconclusive evaluation that pends to a
/// human. It must never deny on its own account for an infrastructure reason.
/// </summary>
public interface IClinicalCriteriaProvider
{
    /// <summary>
    /// Stable key used in configuration and persisted on every evaluation:
    /// "cho-local", "interqual", "mcg".
    /// </summary>
    string ProviderId { get; }

    /// <summary>Human-readable name for operations screens and audit output.</summary>
    string DisplayName { get; }

    /// <summary>
    /// False when the provider cannot be used as deployed — no licence, no
    /// endpoint, no credential. The service skips an unconfigured provider and
    /// records "not-configured" rather than calling it.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Evaluate one request against this provider's guidelines.</summary>
    Task<CriteriaEvaluation> EvaluateAsync(CriteriaRequest request, CancellationToken ct = default);
}

/// <summary>
/// The entry point callers use. Resolves the provider for a request from
/// configuration, applies the evaluation timeout, and never throws: an
/// unreachable or unconfigured provider yields an inconclusive evaluation,
/// which routes the request to a human reviewer.
/// </summary>
public interface IClinicalCriteriaService
{
    Task<CriteriaEvaluation> EvaluateAsync(CriteriaRequest request, CancellationToken ct = default);

    /// <summary>Which providers are registered and which of them are usable.</summary>
    IReadOnlyList<CriteriaProviderStatus> GetProviderStatus();
}
