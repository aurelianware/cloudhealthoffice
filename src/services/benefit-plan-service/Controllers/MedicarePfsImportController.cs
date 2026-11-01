using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Middleware;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Loads the CMS Medicare Physician Fee Schedule (the quarterly RVU zip, or its
/// PPRRVU and GPCI CSVs) into the fee schedule store claims are priced from,
/// one schedule per selected payment locality. Dry run by default; writing is
/// tenant configuration (<c>settings:manage</c>).
/// See docs/integration/MEDICARE-PFS-IMPORTER.md.
/// </summary>
[ApiController]
[Route("api/v1/fee-schedules/import/medicare-pfs")]
public sealed class MedicarePfsImportController : ControllerBase
{
    private const long MaxUploadBytes = 200L * 1024 * 1024;

    private readonly IMedicarePfsImportService _importer;
    private readonly ICurrentActor _actor;

    public MedicarePfsImportController(IMedicarePfsImportService importer, ICurrentActor actor)
    {
        _importer = importer;
        _actor = actor;
    }

    [HttpPost]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(MedicarePfsImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MedicarePfsImportReport>> Import(
        IFormFile? rvuZip,
        IFormFile? pprrvu,
        IFormFile? gpci,
        [FromQuery] int? year,
        [FromQuery] string? localities = null,
        [FromQuery] string? states = null,
        [FromQuery] bool allLocalities = false,
        [FromQuery] DateTime? effectiveDate = null,
        [FromQuery] DateTime? termDate = null,
        [FromQuery] string? release = null,
        [FromQuery] decimal? conversionFactor = null,
        [FromQuery] bool qpp = false,
        [FromQuery] Guid? planId = null,
        [FromQuery] bool replace = false,
        [FromQuery] bool dryRun = true,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });

        var errors = new List<string>();
        if (year is null or < 1992 or > 2100) errors.Add("year is required (the PFS calendar year, e.g. 2026).");
        if (rvuZip is null && (pprrvu is null || gpci is null))
            errors.Add("Upload the CMS RVU zip as 'rvuZip', or the PPRRVU and GPCI CSVs as 'pprrvu' and 'gpci'.");
        if (termDate is { } t && (effectiveDate ?? (year is { } y ? new DateTime(y, 1, 1) : t)).Date >= t.Date)
            errors.Add("termDate must be after the effective date.");
        var options = new MedicarePfsImportOptions
        {
            Year = year ?? 0,
            EffectiveDate = effectiveDate,
            TermDate = termDate,
            Release = release,
            ConversionFactor = conversionFactor,
            Localities = Split(localities),
            States = Split(states),
            AllLocalities = allLocalities,
            PlanId = planId,
            Replace = replace,
            DryRun = dryRun,
        };
        if (!options.HasSelector)
            errors.Add("Choose localities (MAC-locality keys such as 04412-18), states (TX,CA), or allLocalities=true.");
        if (errors.Count > 0) return BadRequest(new { message = "Invalid Medicare PFS import.", details = errors });

        try
        {
            Stream p, g;
            if (pprrvu is not null && gpci is not null)
            {
                p = FeeScheduleCsvReader.OpenCsv(pprrvu.OpenReadStream(), pprrvu.FileName);
                g = FeeScheduleCsvReader.OpenCsv(gpci.OpenReadStream(), gpci.FileName);
            }
            else
            {
                (p, g, _, _) = MedicarePfsFiles.OpenRvuZip(rvuZip!.OpenReadStream(), qpp);
            }

            await using (p)
            await using (g)
            {
                var report = await _importer.ImportAsync(p, g, options, tenantId, _actor.UserId, ct);
                report.PprrvuFile = pprrvu?.FileName ?? rvuZip?.FileName;
                report.GpciFile = gpci?.FileName ?? rvuZip?.FileName;
                return Ok(report);
            }
        }
        catch (FeeScheduleFormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.IO.InvalidDataException ex)
        {
            return BadRequest(new { message = $"Could not read the upload: {ex.Message}" });
        }
        catch (FeeSchedulePlanNotFoundException ex)
        {
            return NotFound(new { message = ex.Message, planId = ex.PlanId });
        }
        catch (FeeScheduleConcurrencyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private static HashSet<string> Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
