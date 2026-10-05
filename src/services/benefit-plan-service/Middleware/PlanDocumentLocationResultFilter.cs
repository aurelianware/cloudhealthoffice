using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BenefitPlanService.Middleware;

/// <summary>
/// Global MVC result filter: before any controller response carrying plan
/// documents is written, stored document locations that fail
/// <see cref="PlanDocumentLocationPolicy"/> are replaced with
/// <c>location: null, locationBlocked: true</c> (and logged). This covers
/// data written before the location rule existed and plans served by
/// external adapters, so a bad stored value is never handed to a client as
/// a link.
/// </summary>
public sealed class PlanDocumentLocationResultFilter : IAsyncResultFilter
{
    private readonly PlanDocumentLocationPolicy _policy;

    public PlanDocumentLocationResultFilter(PlanDocumentLocationPolicy policy)
    {
        _policy = policy;
    }

    public Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: not null } result)
        {
            result.Value = Sanitize(result.Value);
        }
        return next();
    }

    /// <summary>
    /// Sanitizes <paramref name="value"/> and returns what should be
    /// written. A lazily-evaluated plan sequence is materialized first so
    /// the sanitized objects are the ones serialized.
    /// </summary>
    public object Sanitize(object value)
    {
        switch (value)
        {
            case BenefitPlan plan:
                return _policy.SanitizeForRead(plan);
            case MemberBenefitView view:
                return _policy.SanitizeForRead(view);
            case PlanVersionPage page:
                foreach (var p in page.Items) if (p is not null) _policy.SanitizeForRead(p);
                return page;
            case IEnumerable<BenefitPlan> plans:
                var list = plans as List<BenefitPlan> ?? plans.ToList();
                foreach (var p in list) if (p is not null) _policy.SanitizeForRead(p);
                return list;
            default:
                return value;
        }
    }
}
