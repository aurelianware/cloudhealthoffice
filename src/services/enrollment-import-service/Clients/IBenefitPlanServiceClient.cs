namespace EnrollmentImportService.Clients;

/// <summary>
/// Resolves a trading partner's own 834 plan code (HD04, "PlanCoverageDescription"
/// in the parsed enrollment) to benefit-plan-service's canonical PlanId. Employers
/// assign plan codes per their own trading-partner agreement — they don't and
/// shouldn't know this platform's internal PlanId — so ProcessCoverageAsync
/// resolves through this crosswalk instead of writing the raw code straight
/// into Coverage.PlanId.
/// </summary>
public interface IBenefitPlanServiceClient
{
    /// <summary>Null when no mapping exists for this (group, insurance line, external code) triple.</summary>
    Task<string?> ResolvePlanIdAsync(
        string tenantId, string groupNumber, string insuranceLineCode, string externalPlanCode,
        CancellationToken ct = default);
}
