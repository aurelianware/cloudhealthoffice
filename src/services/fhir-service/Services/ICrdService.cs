using FhirService.Models;

namespace FhirService.Services;

public interface ICrdService
{
    Task<CrdEvaluationResult> EvaluateCoverageRequirementsAsync(
        CrdHookRequest request,
        string tenantId,
        CancellationToken ct = default);

    // Classification management
    CrdCodeClassification GetClassification(string tenantId);
    CrdCodeClassification? GetClassificationOrNull(string tenantId);
    void SetClassification(string tenantId, CrdCodeClassification classification);
}
