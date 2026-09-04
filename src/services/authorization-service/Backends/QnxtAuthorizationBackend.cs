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
/// A missing/stubbed QNXT integration is an <em>integration-capability</em> gap,
/// not a Cloud Health Office <em>product-capability</em> gap: the same workflow
/// passes on <see cref="ChoAuthorizationBackend"/> in Replace mode.
/// </summary>
/// <remarks>
/// TODO(qnxt-authorization): integrate with the QNXT authorization transaction
/// API (AUTH_INQ / AUTH_CREATE on the QNXT UM stack). Future sibling backends
/// (FacetsAuthorizationBackend, HealthEdgeAuthorizationBackend) implement this
/// same interface; do not create empty classes for them until an engagement
/// needs one.
/// </remarks>
public sealed class QnxtAuthorizationBackend : IAuthorizationBackend
{
    public const string Key = "qnxt";

    private const string Todo =
        "QNXT authorization backend not yet implemented. " +
        "TODO(qnxt-authorization): integrate with the QNXT authorization " +
        "transaction API (AUTH_INQ / AUTH_CREATE). This is per-engagement " +
        "integration work; Cloud Health Office Replace mode serves the same " +
        "workflow natively via ChoAuthorizationBackend.";

    public string BackendKey => Key;

    /// <summary>Augment mode: the external core (QNXT) remains authoritative.</summary>
    public bool IsAuthoritative => false;

    public Task<Authorization> CreateAsync(Authorization authorization, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<Authorization?> GetByNumberAsync(string authorizationNumber, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<Authorization> UpdateStatusAsync(
        Authorization authorization,
        AuthorizationStatus status,
        string? reviewDecision,
        string? reason,
        CancellationToken ct = default)
        => throw new NotImplementedException(Todo);
}
