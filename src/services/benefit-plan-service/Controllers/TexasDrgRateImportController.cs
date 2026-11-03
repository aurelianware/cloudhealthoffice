using BenefitPlanService.Import.MedicaidDrg;
using BenefitPlanService.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Loads HHSC's Texas Medicaid APR-DRG inputs into the Medicaid DRG rate
/// store: APR-DRG weight tables (PDF, .xlsx or .xls) and the urban and rural
/// hospital SDA files (PDF). Upload any mix as <c>files</c>; each is
/// recognised by its content. Dry run by default; writing is tenant
/// configuration (<c>settings:manage</c>).
/// See docs/integration/MEDICAID-DRG-PRICING.md.
/// </summary>
[ApiController]
[Route("api/v1/medicaid-drg/import/texas")]
public sealed class TexasDrgRateImportController : ControllerBase
{
    private const long MaxUploadBytes = 50L * 1024 * 1024;
    private const int MaxFiles = 30;

    private readonly ITexasDrgRateImportService _importer;
    private readonly ITexasInterimRateImportService _interimImporter;
    private readonly ITexasPsychPerDiemImportService _psychImporter;
    private readonly ICurrentActor _actor;

    public TexasDrgRateImportController(
        ITexasDrgRateImportService importer, ITexasInterimRateImportService interimImporter,
        ITexasPsychPerDiemImportService psychImporter, ICurrentActor actor)
    {
        _importer = importer;
        _interimImporter = interimImporter;
        _psychImporter = psychImporter;
        _actor = actor;
    }

    [HttpPost]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(TexasDrgImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TexasDrgImportReport>> Import(
        [FromForm] List<IFormFile>? files,
        [FromQuery] bool replace = false,
        [FromQuery] bool dryRun = true,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });
        if (files is null || files.Count == 0)
            return BadRequest(new { message = "Upload one or more HHSC files as 'files': APR-DRG weight tables (PDF or Excel) and SDA files (PDF)." });
        if (files.Count > MaxFiles)
            return BadRequest(new { message = $"At most {MaxFiles} files per request." });

        var uploads = new List<TexasDrgUpload>();
        try
        {
            foreach (var f in files)
                uploads.Add(new TexasDrgUpload(f.FileName, f.OpenReadStream()));

            var report = await _importer.ImportAsync(uploads, new TexasDrgImportOptions { Replace = replace, DryRun = dryRun }, tenantId, _actor.UserId, ct);
            return Ok(report);
        }
        catch (TexasSdaFormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        finally
        {
            foreach (var u in uploads) await u.Content.DisposeAsync();
        }
    }

    /// <summary>
    /// Loads hospital interim rates (inpatient and outpatient cost-to-charge
    /// ratios) from HHSC's monthly inpatient RCC PDF ({yyyy}-{m}-inp-rcc.pdf),
    /// or from a CSV or Excel sheet: NPI, inpatient and/or outpatient
    /// rate (0.452 or 45.2%), effective date (or <paramref name="effectiveDate"/>
    /// for the whole file), and optionally TPI and hospital name. Texas DRG
    /// pricing uses the inpatient rate for outliers (clients under 21).
    /// </summary>
    [HttpPost("interim-rates")]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(InterimRateImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InterimRateImportReport>> ImportInterimRates(
        IFormFile? file,
        [FromQuery] DateTime? effectiveDate = null,
        [FromQuery] bool replace = false,
        [FromQuery] bool dryRun = true,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Upload HHSC's inpatient RCC PDF, or an interim rate sheet (CSV or Excel), as 'file'." });

        try
        {
            await using var content = file.OpenReadStream();
            var report = await _interimImporter.ImportAsync(
                content, file.FileName,
                new InterimRateImportOptions { EffectiveDate = effectiveDate, Replace = replace, DryRun = dryRun },
                tenantId, _actor.UserId, ct);
            return Ok(report);
        }
        catch (InterimRateFormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Loads psychiatric hospital per diems from HHSC's
    /// "SFY yyyy Psychiatric Hospital Rates" PDF
    /// ({yyyy}-ffy-psychiatric-hosp-per-diem-rates.pdf): private freestanding
    /// and state-owned facilities, one per diem per hospital.
    /// </summary>
    [HttpPost("psychiatric-per-diems")]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(PsychPerDiemImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PsychPerDiemImportReport>> ImportPsychiatricPerDiems(
        IFormFile? file,
        [FromQuery] bool replace = false,
        [FromQuery] bool dryRun = true,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Upload HHSC's psychiatric hospital per diem PDF as 'file'." });

        try
        {
            await using var content = file.OpenReadStream();
            var report = await _psychImporter.ImportAsync(
                content, file.FileName, new PsychPerDiemImportOptions { Replace = replace, DryRun = dryRun }, tenantId, _actor.UserId, ct);
            return Ok(report);
        }
        catch (InterimRateFormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
