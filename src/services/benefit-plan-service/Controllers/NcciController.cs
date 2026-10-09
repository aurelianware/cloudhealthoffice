using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using CloudHealthOffice.NcciEngine.Data;
using CloudHealthOffice.NcciEngine.Import;
using BenefitPlanService.Middleware;
using CloudHealthOffice.Infrastructure.Security;
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
///   POST /api/v1/ncci/cms-load           — load one public CMS PTP/MUE quarterly file
/// </summary>
[ApiController]
[Route("api/v1/ncci")]
public class NcciController : ControllerBase
{
    private readonly INcciEditService _ncciService;
    private readonly INcciQuarterlyLoader _loader;
    private readonly ILogger<NcciController> _logger;

    public NcciController(
        INcciEditService ncciService,
        INcciQuarterlyLoader loader,
        ILogger<NcciController> logger)
    {
        _ncciService = ncciService;
        _loader = loader;
        _logger = logger;
    }

    /// <summary>
    /// Load one public CMS NCCI quarterly file, as CMS publishes it, into
    /// the tenant's NCCI/MUE tables. Upload the extracted file (the
    /// tab-delimited PTP <c>.txt</c>, or the MUE <c>.csv</c>) as
    /// multipart field <c>file</c>. Idempotent: an identical file for the
    /// same quarter/kind/setting/part is reported as <c>alreadyLoaded</c>
    /// and not rewritten unless <c>force=true</c>. See
    /// docs/engines/NCCI-CMS-QUARTERLY-LOAD.md for where to get the files.
    /// Requires the service's default write permission (settings:manage).
    /// </summary>
    /// <param name="file">The CMS file.</param>
    /// <param name="quarter">CMS release quarter, e.g. 2026Q4.</param>
    /// <param name="kind">ptp or mue.</param>
    /// <param name="setting">practitioner or outpatient-hospital.</param>
    /// <param name="part">Optional part label (f1..f4) for a PTP table split across files.</param>
    /// <param name="force">Reload even when this exact file was already loaded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Load completed (or skipped as already loaded)</response>
    /// <response code="400">Bad parameters, or the file holds no rows in the CMS layout</response>
    [HttpPost("cms-load")]
    [RequestSizeLimit(CmsLoadMaxBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = CmsLoadMaxBytes)]
    [ProducesResponseType<NcciLoadResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NcciLoadResult>> LoadCmsFile(
        IFormFile? file,
        [FromForm] string? quarter,
        [FromForm] string? kind,
        [FromForm] string? setting,
        [FromForm] string? part = null,
        [FromForm] bool force = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "A non-empty CMS file is required (multipart field 'file')." });

        NcciCmsFileKind fileKind;
        switch (kind?.Trim().ToLowerInvariant())
        {
            case "ptp": fileKind = NcciCmsFileKind.Ptp; break;
            case "mue": fileKind = NcciCmsFileKind.Mue; break;
            default: return BadRequest(new { message = "kind must be 'ptp' or 'mue'." });
        }

        var subject = User.FindFirst(ChoClaimTypes.Subject)?.Value;
        try
        {
            await using var stream = file.OpenReadStream();
            var result = await _loader.LoadAsync(new NcciLoadRequest
            {
                TenantId = TenantId,
                Quarter = quarter?.Trim().ToUpperInvariant() ?? string.Empty,
                FileKind = fileKind,
                Setting = setting ?? string.Empty,
                Part = part,
                FileName = file.FileName,
                LoadedBy = subject,
                Force = force,
            }, stream, ct);

            _logger.LogInformation(
                "AUDIT NCCI CMS load: {Kind} {Setting} {Quarter} by {Subject} in tenant {TenantId}: {Loaded} loaded, {Rejected} rejected, already loaded: {AlreadyLoaded}",
                result.FileKind, result.Setting, SanitizeForLog(result.Quarter), SanitizeForLog(subject),
                SanitizeForLog(TenantId), result.RowsLoaded, result.RowsRejected, result.AlreadyLoaded);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidDataException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    // The deployed NGINX ingress caps request bodies at 100m
    // (infrastructure/k8s/nginx-ingress-config.yaml, infrastructure/helm/nginx-ingress-values.yaml);
    // stay under it with room for multipart overhead. CMS splits the
    // practitioner PTP table into parts (f1–f4), each well below this.
    internal const long CmsLoadMaxBytes = 95_000_000;

    /// <summary>
    /// Tenant from the validated token (set by the shared TenantMiddleware).
    /// Never from the X-Tenant-Id header or a request body.
    /// </summary>
    private string TenantId => HttpContext.GetTenantId()
        ?? throw new CloudHealthOffice.Infrastructure.Middleware.TenantContextMissingException();

    /// <summary>
    /// Apply NCCI Column 1/2 bundling edits and MUE unit-limit checks
    /// to a claim before payment.  Returns the scrub result with any
    /// edit failures and suggested CARC/RARC codes.
    /// </summary>
    /// <response code="200">Scrub completed (check Passed property for outcome)</response>
    /// <response code="400">Request validation failed</response>
    [HttpPost("scrub")]
    [RequirePermission("claims:work,benefits:read")]
    [ProducesResponseType<NcciScrubResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NcciScrubResult>> Scrub(
        [FromBody] NcciScrubRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // The edit tables are read for the caller's tenant; a body tenantId is ignored.
        request.TenantId = TenantId;

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
    public async Task<ActionResult<NcciTableVersion>> GetVersion(CancellationToken ct)
    {
        var version = await _ncciService.GetTableVersionAsync(TenantId, ct);

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

        // Imports land in the caller's tenant; a body tenantId is ignored.
        var (pairsWritten, mueWritten) = await _ncciService.ImportQuarterlyUpdateAsync(
            TenantId,
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
        [FromQuery] string quarter = "2025Q1",
        CancellationToken ct = default)
    {
        var tenantId = TenantId;
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
    /// <summary>
    /// Ignored. The import always targets the tenant in the caller's token;
    /// kept so existing clients that still send it do not fail to bind.
    /// </summary>
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
