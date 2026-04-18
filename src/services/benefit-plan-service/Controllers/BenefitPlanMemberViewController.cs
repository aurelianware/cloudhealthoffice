using BenefitPlanService.Middleware;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Dedicated route hosting the portal-facing member view of a benefit plan.
/// Lives under <c>/api/v1/benefit-plans</c> (hyphenated) rather than the
/// legacy <c>/api/v1/plans</c> root so future consolidation of the plan
/// API surface can happen cleanly. See TODO(deprecate-plans-route) on
/// BenefitPlansController.
/// </summary>
[ApiController]
[Route("api/v1/benefit-plans")]
public class BenefitPlanMemberViewController : ControllerBase
{
    private readonly IBenefitViewService _view;
    private readonly ILogger<BenefitPlanMemberViewController> _logger;

    public BenefitPlanMemberViewController(
        IBenefitViewService view,
        ILogger<BenefitPlanMemberViewController> logger)
    {
        _view = view;
        _logger = logger;
    }

    private string TenantId => HttpContext.GetTenantId() ?? throw new InvalidOperationException("Tenant context missing");

    /// <summary>
    /// Return a categorized member-facing view of the plan as of the given
    /// service date. Falls back to today (UTC) when <paramref name="serviceDate"/>
    /// is omitted.
    /// </summary>
    [HttpGet("{planId}/member-view")]
    [ProducesResponseType(typeof(MemberBenefitView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MemberBenefitView>> GetMemberView(
        string planId,
        [FromQuery] DateTime? serviceDate = null)
    {
        var asOf = (serviceDate ?? DateTime.UtcNow).Date;
        _logger.LogInformation(
            "Building member view for plan {PlanId} tenant {TenantId} as of {ServiceDate:yyyy-MM-dd}",
            SanitizeForLog(planId), SanitizeForLog(TenantId), asOf);

        var view = await _view.GetMemberViewAsync(planId, TenantId, asOf);
        if (view == null)
        {
            return NotFound(new { message = $"Benefit plan '{planId}' not found" });
        }

        return Ok(view);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}
