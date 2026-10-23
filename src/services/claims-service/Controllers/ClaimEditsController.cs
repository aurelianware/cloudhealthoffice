using ClaimsService.Audit;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The edits on a pended claim as the reviewer decides them: each edit's
/// identity, what it covers, the override policy that applies to it, and
/// whether the caller may override it. The decisions themselves are sent with
/// the approval (<c>POST api/claims/work-queue/{claimId}/resolve</c>).
/// </summary>
[ApiController]
[Route("api/claims")]
[Produces("application/json")]
public class ClaimEditsController : ControllerBase
{
    private readonly IClaimRepository _claims;
    private readonly EditOverridePolicy _policy;
    private readonly ICurrentActor _actor;

    public ClaimEditsController(IClaimRepository claims, EditOverridePolicy policy, ICurrentActor actor)
    {
        _claims = claims;
        _policy = policy;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    [HttpGet("{claimId}/edits")]
    [AuditClaimAccess(ClaimAccessActions.Edits, ClaimIdRouteValue = "claimId")]
    [ProducesResponseType(typeof(ClaimEditsView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClaimEditsView>> Get(string claimId)
    {
        var claim = await _claims.GetByIdAsync(claimId);
        if (claim is null) return NotFound();

        var canWork = _actor.HasPermission(ClaimsPermissions.Work);
        var canSupervise = _actor.HasPermission(ClaimsPermissions.OverrideEdit);
        var edits = ClaimEditCatalog.Edits(claim.PendDetails).Select(e =>
        {
            var rule = _policy.RuleFor(TenantId, claim.LineOfBusiness, e.Category);
            var canOverride = rule.Level switch
            {
                OverrideLevel.Examiner => canWork,
                OverrideLevel.Supervisor => canWork && canSupervise,
                _ => false,
            };
            return new ClaimEditView(e.EditKey, e.EditType, e.RuleId, e.Category, rule.Description, e.Message,
                e.LineNumbers, e.SuggestedCarc, e.SuggestedRarc, rule.Level, rule.RequiresReason,
                rule.RequiresDocumentation, canOverride);
        }).ToList();

        return Ok(new ClaimEditsView(
            claim.Id,
            PendDetails.ComputeFingerprint(claim.PendDetails),
            edits,
            ReviewerDenialReasons.All));
    }

    [HttpGet("edit-override-policy")]
    [ProducesResponseType(typeof(IEnumerable<EditOverrideRule>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<EditOverrideRule>> Policy([FromQuery] LineOfBusiness lineOfBusiness = LineOfBusiness.Commercial) =>
        Ok(_policy.For(TenantId, lineOfBusiness));
}

public sealed record ClaimEditView(
    string EditKey,
    string EditType,
    string RuleId,
    string Category,
    string PolicyDescription,
    string? Message,
    IReadOnlyList<int> LineNumbers,
    string? SuggestedCarc,
    string? SuggestedRarc,
    OverrideLevel OverrideLevel,
    bool RequiresReason,
    bool RequiresDocumentation,
    bool CanOverride);

public sealed record ClaimEditsView(
    string ClaimId,
    string? PendFingerprint,
    IReadOnlyList<ClaimEditView> Edits,
    IReadOnlyList<ReviewerDenialReasons.Reason> DenialReasons);
