using CHO.TerminologyService.Data;
using CHO.TerminologyService.Models;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace CHO.TerminologyService.Controllers;

/// <summary>
/// TMPPM prior-authorization rules (the portal's PA Rule Explorer) and their
/// ingestion (tools/CloudHealthOffice.TmppmIngestionService). terminology-service
/// is the only reader and writer of these collections; nothing else connects to
/// its database.
/// <list type="bullet">
///   <item>Reads: terminology:read (the default; service tokens satisfy it).
///   Rules, editions and diffs are published state Medicaid policy, the same
///   for every tenant.</item>
///   <item>Writing rules, editions and diffs changes that shared data:
///   platform:admin (tenant roles, even through *:*, and service tokens never
///   hold it).</item>
///   <item>Publishing rules as ConceptMap overrides changes the token tenant's
///   translations only: settings:manage, always into the token's tenant.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/v1/tmppm")]
public class TmppmController : ControllerBase
{
    private const int MaxRulesPerRequest = 20_000;

    private readonly ITmppmStore _store;
    private readonly ILogger<TmppmController> _logger;

    public TmppmController(ITmppmStore store, ILogger<TmppmController> logger)
    {
        _store = store;
        _logger = logger;
    }

    private string? Actor => User.FindFirst(ChoClaimTypes.Subject)?.Value;

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    /// <summary>Rules for a procedure code (<c>code</c>) or a category (<c>category</c>), optionally one state.</summary>
    [HttpGet("rules")]
    public async Task<ActionResult<List<TmppmPaRule>>> GetRules(
        [FromQuery] string? code, [FromQuery] string? category, [FromQuery] string? state, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(code))
            return Ok(await _store.SearchByCodeAsync(code, state, ct));
        if (!string.IsNullOrWhiteSpace(category))
            return Ok(await _store.GetRulesByCategoryAsync(category, state, ct));
        return BadRequest(new { error = "code or category is required" });
    }

    [HttpGet("categories")]
    public async Task<ActionResult<List<TmppmCategoryGroup>>> GetCategories([FromQuery] string state = "TX", CancellationToken ct = default)
        => Ok(await _store.GetCategoriesAsync(state, ct));

    [HttpGet("codes")]
    public async Task<ActionResult<List<string>>> AutocompleteCodes([FromQuery] string prefix = "", [FromQuery] int max = 10, CancellationToken ct = default)
        => Ok(await _store.AutocompleteCodeAsync(prefix, Math.Clamp(max, 1, 50), ct));

    [HttpGet("editions")]
    public async Task<ActionResult<List<TmppmEdition>>> GetEditions([FromQuery] int limit = 12, CancellationToken ct = default)
        => Ok(await _store.GetEditionsAsync(Math.Clamp(limit, 1, 120), ct));

    /// <summary>The most recently ingested edition; 404 when none has been ingested.</summary>
    [HttpGet("editions/current")]
    public async Task<ActionResult<TmppmEdition>> GetCurrentEdition(CancellationToken ct)
    {
        var current = (await _store.GetEditionsAsync(1, ct)).FirstOrDefault();
        return current is null ? NotFound() : Ok(current);
    }

    [HttpGet("diffs")]
    public async Task<ActionResult<TmppmDiffReport>> GetDiff([FromQuery] string from, [FromQuery] string to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return BadRequest(new { error = "from and to are required" });
        var diff = await _store.GetDiffAsync(from, to, ct);
        return diff is null ? NotFound() : Ok(diff);
    }

    /// <summary>Upserts rules by RuleId. Shared data: platform:admin.</summary>
    [HttpPut("rules")]
    [RequirePermission(TerminologyController.GlobalWritePermission)]
    public async Task<IActionResult> UpsertRules([FromBody] List<TmppmPaRule> rules, CancellationToken ct)
    {
        if (rules is null || rules.Count == 0)
            return BadRequest(new { error = "At least one rule is required" });
        if (rules.Count > MaxRulesPerRequest)
            return BadRequest(new { error = $"At most {MaxRulesPerRequest} rules per request" });
        if (rules.Any(r => r is null || string.IsNullOrWhiteSpace(r.RuleId)))
            return BadRequest(new { error = "Every rule needs a ruleId" });

        var count = await _store.UpsertRulesAsync(rules, ct);
        _logger.LogInformation("AUDIT tmppm rules upserted: {Count} by {Subject}", count, Sanitize(Actor));
        return Ok(new { upserted = count });
    }

    /// <summary>Saves an edition (by id). Shared data: platform:admin.</summary>
    [HttpPut("editions/{editionId}")]
    [RequirePermission(TerminologyController.GlobalWritePermission)]
    public async Task<IActionResult> SaveEdition(string editionId, [FromBody] TmppmEdition edition, CancellationToken ct)
    {
        if (edition is null)
            return BadRequest(new { error = "An edition body is required" });
        edition.EditionId = editionId;
        await _store.SaveEditionAsync(edition, ct);
        _logger.LogInformation("AUDIT tmppm edition {EditionId} saved by {Subject}", Sanitize(editionId), Sanitize(Actor));
        return Ok(edition);
    }

    /// <summary>Stores a diff report. Shared data: platform:admin.</summary>
    [HttpPost("diffs")]
    [RequirePermission(TerminologyController.GlobalWritePermission)]
    public async Task<IActionResult> SaveDiff([FromBody] TmppmDiffReport diff, CancellationToken ct)
    {
        if (diff is null || string.IsNullOrWhiteSpace(diff.FromEdition) || string.IsNullOrWhiteSpace(diff.ToEdition))
            return BadRequest(new { error = "fromEdition and toEdition are required" });
        await _store.SaveDiffAsync(diff, ct);
        _logger.LogInformation("AUDIT tmppm diff {From} -> {To} saved by {Subject}",
            Sanitize(diff.FromEdition), Sanitize(diff.ToEdition), Sanitize(Actor));
        return Ok();
    }

    /// <summary>
    /// Publishes rules as the token tenant's ConceptMap overrides
    /// (settings:manage, the default write permission). Never another tenant's.
    /// </summary>
    [HttpPost("overrides")]
    public async Task<ActionResult<TmppmPublishOverridesResult>> PublishOverrides(
        [FromBody] TmppmPublishOverridesRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.EditionId) || request.Rules.Count == 0)
            return BadRequest(new { error = "editionId and at least one rule are required" });
        if (request.Rules.Count > MaxRulesPerRequest)
            return BadRequest(new { error = $"At most {MaxRulesPerRequest} rules per request" });

        var tenant = HttpContext.GetTenantId();
        var result = await _store.PublishOverridesAsync(tenant, request.EditionId, request.Rules, Actor, ct);
        _logger.LogInformation(
            "AUDIT tmppm overrides published: {Count} for edition {EditionId} in tenant {TenantId} by {Subject}",
            result.OverridesPublished, Sanitize(request.EditionId), Sanitize(tenant), Sanitize(Actor));
        return Ok(result);
    }
}
