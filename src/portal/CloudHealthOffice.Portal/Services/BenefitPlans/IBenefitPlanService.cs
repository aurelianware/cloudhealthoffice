namespace CloudHealthOffice.Portal.Services;

public interface IBenefitPlanService
{
    Task<List<BenefitPlan>> GetBenefitPlansAsync();
    Task<List<BenefitPlanListItem>> SearchBenefitPlansAsync(string? sponsorId = null, string? productType = null);
    Task<BenefitPlanDetails?> GetBenefitPlanByIdAsync(string planId);
    Task<string> CreateBenefitPlanAsync(CreateBenefitPlanRequest request);
    Task UpdateBenefitPlanAsync(string planId, UpdateBenefitPlanRequest request);
    Task AddBenefitAsync(string planId, UpsertPlanBenefitRequest request);
    Task UpdateBenefitAsync(string planId, string benefitId, UpsertPlanBenefitRequest request);
    Task ReplaceNetworkTiersAsync(string planId, IReadOnlyList<PlanNetworkTier> networkTiers);

    /// <summary>
    /// Returns a categorized member-facing view of the plan as of the given
    /// service date. Null when the plan is not found (404) or the service
    /// is unreachable is surfaced as a <see cref="ServiceUnavailableException"/>.
    /// </summary>
    Task<MemberBenefitView?> GetMemberViewAsync(string planId, DateTime serviceDate);
}
