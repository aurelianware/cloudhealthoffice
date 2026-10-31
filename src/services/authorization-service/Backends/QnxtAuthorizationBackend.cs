using AuthorizationService.Models;

namespace AuthorizationService.Backends;

/// <summary>
/// Augment-mode authorization backend for tenants whose prior authorizations
/// live in QNXT (TriZetto / Cognizant). All operations throw
/// <see cref="NotImplementedException"/> with a clear migration TODO until the
/// QNXT integration ships for an engagement.
///
/// This is the external-core integration seam, deliberately kept a documented
/// stub — no fake QNXT SOAP/API client. It is selected only when a tenant is
/// explicitly configured for Augment mode with the "qnxt" backend; it is NEVER
/// a silent fallback. Replace mode does not use it.
///
/// CHO is the reference backend; QNXT uses a different connector implementation
/// tailored per payer engagement, not a universal bundled connector. See
/// docs/adr/027-core-adapter-engagement-boundary.md and the core adapter QRE.
/// Evaluate native capability on <see cref="ChoAuthorizationBackend"/> separately
/// from this engagement's implementation and deployed acceptance.
/// </summary>
/// <remarks>
/// TODO(qnxt-authorization): integrate with the payer's licensed QNXT UM
/// interface (operation names come from that specification, not from this
/// comment; see docs/integration/CORE-ADAPTER-IMPLEMENTER-GUIDE.md). Future sibling backends
/// (FacetsAuthorizationBackend, HealthEdgeAuthorizationBackend) implement this
/// same interface; do not create empty classes for them until an engagement
/// needs one.
/// </remarks>
public sealed class QnxtAuthorizationBackend : IAuthorizationBackend
{
    public const string Key = "qnxt";

    private const string Todo =
        "QNXT authorization backend not yet implemented. " +
        "TODO(qnxt-authorization): integrate with the payer's licensed QNXT " +
        "authorization interface. This is per-engagement " +
        "integration work; Cloud Health Office Replace mode serves the same " +
        "workflow natively via ChoAuthorizationBackend.";

    public string BackendKey => Key;

    /// <summary>Augment mode: the external core (QNXT) remains authoritative.</summary>
    public bool IsAuthoritative => false;

    public Task<Authorization> CreateAsync(Authorization authorization, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<Authorization> RecordAutomatedDecisionAsync(
        Authorization authorization, AutomatedDecision decision, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<Authorization?> GetByNumberAsync(string authorizationNumber, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<Authorization> UpdateStatusAsync(
        Authorization authorization,
        AuthorizationStatus status,
        string? reviewDecision,
        string? reason,
        CancellationToken ct = default,
        AuthorizationDecisionDetails? details = null)
        => throw new NotImplementedException(Todo);
}
