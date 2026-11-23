using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Pended claims: the claims the plan's core had pended when they were loaded,
/// what CHO would have done with each, and, once a later file brings the
/// examiner's decision, whether CHO agreed. By the core's pend reason, so the
/// plan can see which pends CHO could resolve without an examiner. Read only;
/// reading needs <c>claims:import</c>, as the Shadow Adjudication page does.
/// See docs/integration/SHADOW-ADJUDICATION.md#pended-claims.
/// </summary>
[ApiController]
[Route("api/v1/claims/shadow-adjudication")]
[Produces("application/json")]
public class ShadowPendedController : ControllerBase
{
    private readonly ShadowPendedAnalysisService _analysis;
    private readonly ICurrentActor _actor;

    public ShadowPendedController(ShadowPendedAnalysisService analysis, ICurrentActor actor)
    {
        _analysis = analysis;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>The pended-claims report over shadow results from the last <c>days</c> days (default 365).</summary>
    [HttpGet("pended")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(ShadowPendedAnalysis), StatusCodes.Status200OK)]
    public async Task<ActionResult<ShadowPendedAnalysis>> Get([FromQuery] int days = 365, CancellationToken ct = default) =>
        Ok(await _analysis.BuildAsync(TenantId, days, ct));
}
