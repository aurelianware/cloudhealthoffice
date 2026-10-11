using Hl7.Fhir.Model;

namespace FhirService.Services;

public interface IPasAutoAdjudicator
{
    /// <param name="claim">The PAS Claim being decided.</param>
    /// <param name="context">The request bundle. Read for clinical context only.</param>
    /// <param name="tenantId">
    /// The AUTHENTICATED tenant of the request (from the validated token). It
    /// selects the tenant's PA rule set. Never taken from the bundle: a
    /// <c>meta.tag</c> in the request body is caller-controlled (D-12).
    /// </param>
    /// <param name="timeoutMs">Time budget; the adjudicator pends when it runs out.</param>
    /// <param name="ct">Request cancellation.</param>
    Task<Models.PasDecisionResult> TryDecideAsync(
        Claim claim,
        Bundle context,
        string tenantId,
        int timeoutMs,
        CancellationToken ct = default);
}
