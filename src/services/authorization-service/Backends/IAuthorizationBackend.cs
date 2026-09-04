using AuthorizationService.Models;

namespace AuthorizationService.Backends;

/// <summary>
/// The authorization system-of-record seam.
///
/// Architecture
/// ============
/// The FHIR/PAS and controller layers depend on the authorization
/// application workflow, which resolves an <see cref="IAuthorizationBackend"/>
/// by operating mode (see <see cref="IAuthorizationBackendSelector"/>). They do
/// NOT choose a vendor-specific type directly, so core-system concerns never
/// leak upward.
///
///   FHIR / PAS  ->  authorization workflow  ->  IAuthorizationBackend
///                                                 |- ChoAuthorizationBackend   (Replace, CHO-native)
///                                                 `- QnxtAuthorizationBackend  (Augment, external core)
///
/// Replace mode  = Cloud Health Office owns the record (authoritative).
/// Augment mode  = Cloud Health Office fronts an external core (QNXT / Facets /
///                 HealthEdge); that core remains authoritative.
///
/// This is a repository-native seam, not a new framework: the CHO backend is a
/// thin application layer over the existing <c>IAuthorizationRepository</c>.
/// </summary>
public interface IAuthorizationBackend
{
    /// <summary>Backend identifier, e.g. "cho", "qnxt".</summary>
    string BackendKey { get; }

    /// <summary>
    /// True when this backend owns the authoritative record (Replace mode).
    /// False when it fronts an external authoritative core (Augment mode).
    /// </summary>
    bool IsAuthoritative { get; }

    /// <summary>Create/submit a prior authorization and return the persisted record.</summary>
    Task<Authorization> CreateAsync(Authorization authorization, CancellationToken ct = default);

    /// <summary>Retrieve a prior authorization by its tracking number.</summary>
    Task<Authorization?> GetByNumberAsync(string authorizationNumber, CancellationToken ct = default);

    /// <summary>
    /// Apply a status/decision transition and persist it, preserving the
    /// append-only status history on the record.
    /// </summary>
    Task<Authorization> UpdateStatusAsync(
        Authorization authorization,
        AuthorizationStatus status,
        string? reviewDecision,
        string? reason,
        CancellationToken ct = default);
}
