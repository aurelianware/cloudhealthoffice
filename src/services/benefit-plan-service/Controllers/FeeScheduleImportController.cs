using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Middleware;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Imports a flat-rate fee schedule CSV (a state Medicaid fee schedule such as
/// TMHP's or Medi-Cal's, a plan's contracted rate sheet, a Medicare schedule
/// already priced per locality) into the fee schedule store claims are priced
/// from. With <c>planId</c> it becomes that plan's default schedule, which
/// prices lines whose provider has no contract; without, it is a standalone
/// schedule a provider contract term names by the returned id.
///
/// A dry run (the default) writes nothing and reports the columns read, the
/// lines it would load, skip reasons and conflicting duplicate rates.
/// Writing is tenant configuration: <c>settings:manage</c>.
/// See docs/integration/FEE-SCHEDULE-IMPORTER.md.
/// </summary>
[ApiController]
[Route("api/v1/fee-schedules/import/csv")]
public sealed class FeeScheduleImportController : ControllerBase
{
    /// <summary>A state's full fee schedule export is a few MB; 200 MB leaves room for multi-program files.</summary>
    private const long MaxUploadBytes = 200L * 1024 * 1024;

    private static readonly FeeScheduleType[] ImportableTypes =
        [FeeScheduleType.Medicaid, FeeScheduleType.Commercial, FeeScheduleType.Custom];

    private readonly IFeeScheduleImportService _importer;
    private readonly ICurrentActor _actor;

    public FeeScheduleImportController(IFeeScheduleImportService importer, ICurrentActor actor)
    {
        _importer = importer;
        _actor = actor;
    }

    [HttpPost]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(FeeScheduleImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeeScheduleImportReport>> Import(
        IFormFile? file,
        [FromQuery] string? name,
        [FromQuery] DateTime? effectiveDate,
        [FromQuery] DateTime? termDate = null,
        [FromQuery] FeeScheduleType type = FeeScheduleType.Custom,
        [FromQuery] FeeScheduleSourceType? sourceType = null,
        [FromQuery] string? sourceId = null,
        [FromQuery] string? sourceVersion = null,
        [FromQuery] string? jurisdiction = null,
        [FromQuery] Guid? planId = null,
        [FromQuery(Name = "filter")] string[]? filter = null,
        [FromQuery] string? codeColumn = null,
        [FromQuery] string? rateColumn = null,
        [FromQuery] string? facilityRateColumn = null,
        [FromQuery] string? modifierColumn = null,
        [FromQuery] string? revenueCodeColumn = null,
        [FromQuery] DuplicateRatePolicy onDuplicate = DuplicateRatePolicy.Reject,
        [FromQuery] bool keepZeroRates = false,
        [FromQuery] bool replace = false,
        [FromQuery] bool dryRun = true,
        [FromQuery] bool includeLines = false,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });

        var errors = new List<string>();
        if (file is null || file.Length == 0) errors.Add("Upload the fee schedule as 'file' (CSV, or a .zip holding one).");
        if (string.IsNullOrWhiteSpace(name)) errors.Add("name is required.");
        if (effectiveDate is null) errors.Add("effectiveDate is required (the date the rates take effect).");
        if (termDate is { } t && effectiveDate is { } e && t.Date <= e.Date) errors.Add("termDate must be after effectiveDate.");
        if (!ImportableTypes.Contains(type))
            errors.Add($"type must be Medicaid, Commercial or Custom; {type} schedules are not flat-rate files.");
        if (!string.IsNullOrWhiteSpace(jurisdiction) && jurisdiction.Trim().Length != 2)
            errors.Add("jurisdiction is a two-letter state code.");

        var filters = new List<(string, string)>();
        foreach (var f in filter ?? [])
        {
            var eq = f.IndexOf('=');
            if (eq <= 0 || eq == f.Length - 1) { errors.Add($"filter '{f}' must be Column=Value."); continue; }
            filters.Add((f[..eq].Trim(), f[(eq + 1)..].Trim()));
        }
        if (errors.Count > 0) return BadRequest(new { message = "Invalid fee schedule import.", details = errors });

        var options = new FeeScheduleImportOptions
        {
            Name = name!.Trim(),
            EffectiveDate = effectiveDate!.Value,
            TermDate = termDate,
            Type = type,
            SourceType = sourceType,
            SourceId = sourceId,
            SourceVersion = sourceVersion,
            Jurisdiction = jurisdiction,
            PlanId = planId,
            Filters = filters,
            Columns = new FeeScheduleColumnOverrides(codeColumn, rateColumn, facilityRateColumn, modifierColumn, revenueCodeColumn),
            OnDuplicate = onDuplicate,
            KeepZeroRates = keepZeroRates,
            Replace = replace,
            DryRun = dryRun,
            IncludeLines = includeLines,
        };

        try
        {
            await using var csv = FeeScheduleCsvReader.OpenCsv(file!.OpenReadStream(), file.FileName);
            var report = await _importer.ImportAsync(csv, options, tenantId, _actor.UserId, ct);
            return Ok(report);
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
}
