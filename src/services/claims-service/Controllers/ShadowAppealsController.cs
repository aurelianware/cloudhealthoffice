using System.Text;
using ClaimsService.Services.Adjudication.Shadow;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Shadow adjudication for appeals: CHO's shadow reading of an appealed claim,
/// shown to the appeal's reviewer, and the plan's appeal outcomes against
/// CHO's readings (how often appeals of denials CHO would have paid were
/// overturned). Advisory only: CHO never decides, files or answers an appeal.
/// See docs/integration/SHADOW-ADJUDICATION.md#appeals.
/// </summary>
[ApiController]
[Route("api/v1/claims/shadow-adjudication")]
[Produces("application/json")]
public class ShadowAppealsController : ControllerBase
{
    private readonly ShadowAppealService _appeals;
    private readonly ICurrentActor _actor;

    public ShadowAppealsController(ShadowAppealService appeals, ICurrentActor actor)
    {
        _appeals = appeals;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// The latest shadow result for a claim, by its claim number, with CHO's
    /// advice (would pay, denies for another reason, confirms, ...) and the
    /// overturn rate of earlier appeals on the same CARC. 404 when the claim
    /// has not been shadowed. Needs <c>claims:read</c>, which appeal reviewers have.
    /// </summary>
    [HttpGet("claims/{claimNumber}/appeal-evidence")]
    [ProducesResponseType(typeof(ShadowAppealEvidence), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShadowAppealEvidence>> Evidence(string claimNumber, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claimNumber)) return NotFound();
        var evidence = await _appeals.EvidenceAsync(TenantId, claimNumber, ct);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    /// <summary>
    /// Loads the plan's appeal outcomes (CSV, header row first): Claim number,
    /// Appeal number, Appeal level, Decision (overturned, partially overturned,
    /// upheld, withdrawn), Decision date, Denial code. Reloading is safe: an
    /// outcome for the same claim and appeal number replaces the earlier one.
    /// </summary>
    [HttpPost("appeal-outcomes/import")]
    [RequirePermission(ClaimsPermissions.Import)]
    [RequestSizeLimit(50_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 50_000_000)]
    [ProducesResponseType(typeof(ShadowAppealOutcomeImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ShadowAppealOutcomeImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ShadowAppealOutcomeImportResult>> Import(
        [FromForm] IFormFile file, [FromForm] bool dryRun = false, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty appeal outcomes file is required." });

        string content;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8))
            content = await reader.ReadToEndAsync(ct);

        var result = await _appeals.ImportAsync(TenantId, content, dryRun, _actor.UserId, ct);
        return result.FileErrors.Count > 0 ? UnprocessableEntity(result) : Ok(result);
    }

    /// <summary>Appeal outcomes by CHO's advice and by CARC.</summary>
    [HttpGet("appeal-outcomes/summary")]
    [RequirePermission(ClaimsPermissions.Import)]
    [ProducesResponseType(typeof(ShadowAppealSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<ShadowAppealSummary>> Summary(CancellationToken ct = default) =>
        Ok(await _appeals.SummaryAsync(TenantId, ct));
}
