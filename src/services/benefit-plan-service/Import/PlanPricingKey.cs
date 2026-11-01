using BenefitPlanService.Repositories;

namespace BenefitPlanService.Import;

/// <summary>
/// The plan key contracts and plan default fee schedules are stored under, so
/// claims find them (<c>PricingRequest.PlanId</c>, then <c>PlanCode</c>):
/// <list type="bullet">
///   <item>A plan code (<c>BenefitPlan.PlanId</c>, e.g. <c>STAR-TX-01</c>) is
///   the same on every version of the plan, so rates keyed to it survive an
///   amendment, which gives the plan a new version id. Use it unless the rates
///   belong to one version only.</item>
///   <item>A plan version id (a GUID) is that version only. It wins over the
///   plan code for claims on that version.</item>
/// </list>
/// </summary>
public static class PlanPricingKey
{
    /// <summary>The key for <paramref name="text"/>, or null when no such plan or version exists in the tenant.</summary>
    public static async Task<string?> ResolveAsync(IBenefitPlanRepository plans, string text, string tenantId)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (Guid.TryParse(text, out var versionId))
            return await plans.GetByIdAsync(versionId.ToString("D"), tenantId) is { } version ? version.Id : null;

        // Any version (draft, future-effective or published) makes the code a plan.
        var (versions, _) = await plans.ListVersionsAsync(text, tenantId, 1, null);
        return versions.Count > 0 ? versions[0].PlanId : null;
    }

    /// <summary>The key as written, without a lookup: a GUID normalized, a plan code trimmed.</summary>
    public static string Normalize(string text) =>
        Guid.TryParse(text.Trim(), out var versionId) ? versionId.ToString("D") : text.Trim();
}
