using AuthorizationService.Models;

namespace AuthorizationService.Adapters;

/// <summary>
/// Per-engagement source-system adapter for prior-authorization records.
///
/// Product / services boundary
/// ===========================
/// Cloud Health Office (CHO) ships the FHIR PAS surface (fhir-service
/// <c>PasController</c>) and the CHO-native authorization store
/// (<c>AuthorizationsController</c> + <c>AuthorizationRepository</c>). The
/// CHO-native path is the default and is what the acceptance harness exercises
/// in Demo/Cho mode.
///
/// This interface names the seam where a customer's prior-authorization system
/// of record (for example QNXT / TriZetto) is bound during an engagement. It
/// intentionally mirrors the adapter pattern already used elsewhere in the
/// platform — <c>IClaimAdapter</c> (claims-service),
/// <c>IProviderAdapter</c> (provider-service), and
/// <c>IBenefitPlanAdapter</c> (benefit-plan-service) — each of which pairs a
/// <c>Cho*Adapter</c> with a per-engagement <c>Qnxt*Adapter</c> stub.
///
/// There is no <c>ChoAuthorizationAdapter</c> in this seam yet: the CHO-native
/// authorization path is served directly by <c>AuthorizationsController</c>
/// today. This interface exists so the QNXT create-auth integration point is
/// explicit and testable rather than implied.
/// </summary>
public interface IAuthorizationAdapter
{
    /// <summary>Source platform key, e.g. "qnxt".</summary>
    string Platform { get; }

    /// <summary>
    /// Create (submit) a prior authorization in the source system of record and
    /// return the persisted record, including the assigned tracking number and
    /// received/decision timestamps.
    /// </summary>
    Task<Authorization> CreateAuthorizationAsync(
        Authorization authorization, CancellationToken ct = default);

    /// <summary>Look up a prior authorization by its tracking number.</summary>
    Task<Authorization?> GetAuthorizationByNumberAsync(
        string tenantId, string authorizationNumber, CancellationToken ct = default);
}
