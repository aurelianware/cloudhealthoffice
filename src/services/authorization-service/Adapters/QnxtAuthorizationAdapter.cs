using AuthorizationService.Models;

namespace AuthorizationService.Adapters;

/// <summary>
/// Stub adapter for tenants whose prior authorizations live in QNXT
/// (TriZetto / Cognizant). All methods throw
/// <see cref="NotImplementedException"/> with a clear migration TODO until the
/// QNXT integration ships for an engagement.
///
/// This mirrors the stub adapters already present in the platform —
/// <c>QnxtClaimAdapter</c> (claims-service),
/// <c>QnxtProviderAdapter</c> (provider-service), and
/// <c>QnxtBenefitPlanAdapter</c> (benefit-plan-service) — which are likewise
/// unimplemented placeholders today.
///
/// Deliberately NOT wired into DI: the CHO-native authorization path
/// (<c>AuthorizationsController</c> + <c>AuthorizationRepository</c>) is the
/// default and is what the CMS-0057-F acceptance harness runs against in
/// Demo/Cho mode. This class exists so scenario PAS-03's QNXT create-auth
/// integration point is explicit and its GAP is asserted by a test rather than
/// silently missing.
/// </summary>
/// <remarks>
/// TODO(qnxt-authorization): integrate with the QNXT authorization transaction
/// API (AUTH_INQ / AUTH_CREATE on the QNXT UM stack). Do not build a fake QNXT
/// SOAP client here — this stub stays a documented placeholder until a real
/// engagement binds it.
/// </remarks>
public class QnxtAuthorizationAdapter : IAuthorizationAdapter
{
    private const string Todo =
        "QNXT authorization adapter not yet implemented. " +
        "TODO(qnxt-authorization): integrate with the QNXT authorization " +
        "transaction API (AUTH_INQ / AUTH_CREATE). CHO ships the FHIR PAS " +
        "surface and the CHO-native authorization store; the QNXT create-auth " +
        "binding is per-engagement work.";

    public string Platform => "qnxt";

    public Task<Authorization> CreateAuthorizationAsync(
        Authorization authorization, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);

    public Task<Authorization?> GetAuthorizationByNumberAsync(
        string tenantId, string authorizationNumber, CancellationToken ct = default)
        => throw new NotImplementedException(Todo);
}
