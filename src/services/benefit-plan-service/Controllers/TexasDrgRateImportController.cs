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
    private readonly ICurrentActor _actor;

    public TexasDrgRateImportController(ITexasDrgRateImportService importer, ICurrentActor actor)
    {
        _importer = importer;
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
}
