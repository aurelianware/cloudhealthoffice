using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using CloudHealthOffice.NcciEngine.Data;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// NCCI/MUE edit endpoints.
///
/// These endpoints are consumed by:
///   1. The Argo claims-adjudication workflow (pre-payment NCCI scrub)
///   2. The portal admin UI (quarterly table import, version status)
///   3. The claims scrubbing service (pre-adjudication checks)
///
/// Endpoint summary:
///   POST /api/v1/ncci/scrub              — scrub a claim against NCCI/MUE edits
///   GET  /api/v1/ncci/version            — table version info for the tenant
///   POST /api/v1/ncci/import             — import a quarterly CMS update
///   POST /api/v1/ncci/seed               — seed baseline data (dev/new tenant)
/// </summary>
[ApiController]
[Route("api/v1/ncci")]
public class NcciController : ControllerBase
{
    private readonly INcciEditService _ncciService;
    private readonly ILogger<NcciController> _logger;

    public NcciController(INcciEditService ncciService, ILogger<NcciController> logger)
    {
        _ncciService = ncciService;
        _logger = logger;
    }

    /// <summary>
    /// Apply NCCI Column 1/2 bundling edits and MUE unit-limit checks
    /// to a claim before payment.  Returns the scrub result with any
    /// edit failures and suggested CARC/RARC codes.
    /// </summary>
    /// <response code="200">Scrub completed (check Passed property for outcome)</response>
    /// <response code="400">Request validation failed</response>
    [HttpPost("scrub")]
    [ProducesResponseType<NcciScrubResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NcciScrubResult>> Scrub(
        [FromBody] NcciScrubRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _ncciService.ScrubAsync(request, ct);

        _logger.LogInformation(
            "NCCI scrub: claim {ClaimId} — {Failures} failures, {PairChecks} pair checks, {MueChecks} MUE checks",
            SanitizeForLog(request.ClaimId), result.EditFailures.Count, result.NcciPairsChecked, result.MueChecked);

        return Ok(result);
    }

    /// <summary>
    /// Get the currently active NCCI/MUE table version for the tenant.
    /// </summary>
    /// <response code="200">Version info returned</response>
    /// <response code="404">No tables have been imported yet</response>
    [HttpGet("version")]
    [ProducesResponseType<NcciTableVersion>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<NcciTableVersion>> GetVersion(
        [FromHeader(Name = "X-Tenant-Id")] string tenantId,
        CancellationToken ct)
    {
        var version = await _ncciService.GetTableVersionAsync(tenantId, ct);

        if (version is null)
            return NotFound(new { message = "No NCCI table version found. Run /api/v1/ncci/seed or /api/v1/ncci/import." });

        return Ok(version);
    }

    /// <summary>
    /// Import a quarterly CMS NCCI/MUE update.
    /// Replaces existing records for the same effective quarter.
    ///
    /// In production this is called by the CHO quarterly-update pipeline
    /// after downloading and parsing the CMS NCCI files.
    /// </summary>
    /// <response code="200">Import completed; returns counts of records written</response>
    [HttpPost("import")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportQuarterly(
        [FromBody] NcciImportRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var (pairsWritten, mueWritten) = await _ncciService.ImportQuarterlyUpdateAsync(
            request.TenantId,
            request.Quarter,
            request.Pairs,
            request.MueEntries,
            ct);

        return Ok(new
        {
            quarter = request.Quarter,
            pairsWritten,
            mueWritten,
            importedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Seed baseline NCCI/MUE data from the built-in Q1 2025 seed set.
    /// Use for new tenant environments or development/testing.
    /// Safe to call multiple times — uses upsert semantics.
    /// </summary>
    /// <response code="200">Seed completed; returns counts of records written</response>
    [HttpPost("seed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Seed(
        [FromHeader(Name = "X-Tenant-Id")] string tenantId,
        [FromQuery] string quarter = "2025Q1",
        CancellationToken ct = default)
    {
        var pairs = NcciSeedData.BuildNcciPairs(tenantId);
        var mues  = NcciSeedData.BuildMueEntries(tenantId);

        var (pairsWritten, mueWritten) = await _ncciService.ImportQuarterlyUpdateAsync(
            tenantId, quarter, pairs, mues, ct);

        _logger.LogInformation(
            "NCCI seed for tenant {TenantId} ({Quarter}): {Pairs} pairs, {Mue} MUE entries",
            SanitizeForLog(tenantId), SanitizeForLog(quarter), pairsWritten, mueWritten);

        return Ok(new
        {
            tenantId,
            quarter,
            pairsWritten,
            mueWritten,
            seedSource = "built-in Q1 2025 baseline",
        });
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}

/// <summary>
/// Request body for the quarterly CMS import endpoint.
/// </summary>
public class NcciImportRequest
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// CMS quarter label, e.g. "2025Q2".
    /// </summary>
    public string Quarter { get; set; } = string.Empty;

    /// <summary>
    /// NCCI Column 1 / Column 2 edit pairs from the CMS quarterly file.
    /// </summary>
    public List<NcciEditPair> Pairs { get; set; } = new();

    /// <summary>
    /// MUE entries from the CMS quarterly file.
    /// </summary>
    public List<MueEntry> MueEntries { get; set; } = new();
}
