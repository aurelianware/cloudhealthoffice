using Hl7.Fhir.Model;

namespace FhirService.Services;

public interface IPasAutoAdjudicator
{
    Task<Models.PasDecisionResult> TryDecideAsync(
        Claim claim,
        Bundle context,
        int timeoutMs,
        CancellationToken ct = default);
}
