using FhirService.Models;

namespace FhirService.Services;

public interface ICrdService
{
    Task<CrdEvaluationResult> EvaluateCoverageRequirementsAsync(
        CrdHookRequest request,
        string tenantId,
        CancellationToken ct = default);
}
