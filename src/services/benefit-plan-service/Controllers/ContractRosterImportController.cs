using BenefitPlanService.Import.Contracts;
using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Middleware;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Imports a provider contract roster CSV into the contracts claims are priced
/// from: each provider's terms (fee schedule, percent of Medicare, percent of
/// billed, case rate, per diem) per plan. The contracts are a pricing copy of
/// the core's own, tagged <c>import:{source}</c> (<c>systemOfRecord=core</c>,
/// the default), or CHO's own legal contracts created in
/// provider-contracts-service (<c>systemOfRecord=cho</c>, which also needs the
/// caller's <c>contracts:write</c>). Dry run by default; writing is tenant
/// configuration (<c>settings:manage</c>).
/// See docs/integration/CONTRACT-ROSTER-IMPORTER.md.
/// </summary>
[ApiController]
[Route("api/v1/pricing-contracts/import/csv")]
public sealed class ContractRosterImportController : ControllerBase
{
    private const long MaxUploadBytes = 200L * 1024 * 1024;

    private readonly IContractRosterImportService _importer;
    private readonly ICurrentActor _actor;

    public ContractRosterImportController(IContractRosterImportService importer, ICurrentActor actor)
    {
        _importer = importer;
        _actor = actor;
    }

    [HttpPost]
    [RequirePermission("settings:manage")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(ContractRosterImportReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ContractRosterImportReport>> Import(
        IFormFile? file,
        [FromQuery] string? source = null,
        [FromQuery] bool replaceNonImported = false,
        [FromQuery] bool dryRun = true,
        [FromQuery] bool includeContracts = false,
        [FromQuery] ContractSystemOfRecord systemOfRecord = ContractSystemOfRecord.Core,
        CancellationToken ct = default)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrWhiteSpace(tenantId))
            return BadRequest(new { message = "Tenant context missing." });
        if (file is null || file.Length == 0)
            return BadRequest(new { message = "Upload the contract roster as 'file' (CSV, or a .zip holding one)." });

        var options = new ContractRosterImportOptions
        {
            Source = string.IsNullOrWhiteSpace(source) ? "csv" : source,
            ReplaceNonImported = replaceNonImported,
            DryRun = dryRun,
            IncludeContracts = includeContracts,
            SystemOfRecord = systemOfRecord,
        };

        try
        {
            await using var csv = FeeScheduleCsvReader.OpenCsv(file.OpenReadStream(), file.FileName);
            return Ok(await _importer.ImportAsync(csv, options, tenantId, _actor.UserId, ct));
        }
        catch (FeeScheduleFormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.IO.InvalidDataException ex)
        {
            return BadRequest(new { message = $"Could not read the upload: {ex.Message}" });
        }
        catch (FeeScheduleConcurrencyException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
