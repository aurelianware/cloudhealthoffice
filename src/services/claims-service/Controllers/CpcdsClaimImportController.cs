using System.Text.Json;
using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// The CPCDS file drop: loads claims the plan's core admin system adjudicated
/// (QNXT, Facets, HealthRules, or anything that writes the CARIN Common Payer
/// Consumer Data Set) so CHO's Patient Access and Provider Access APIs serve
/// them as CARIN Blue Button ExplanationOfBenefits. See
/// <c>docs/integration/CPCDS-FILE-DROP-IMPORTER.md</c>.
/// </summary>
[ApiController]
[Route("api/v1/claims/import/cpcds")]
[Produces("application/json")]
public class CpcdsClaimImportController : ControllerBase
{
    private readonly ICpcdsClaimImportService _import;
    private readonly ICurrentActor _actor;

    public CpcdsClaimImportController(ICpcdsClaimImportService import, ICurrentActor actor)
    {
        _import = import;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Loads one CPCDS claim file. Reloading a file is safe: unchanged claims
    /// are left alone, changed ones get a new version. Set <c>dryRun</c> to see
    /// the report without writing anything.
    /// </summary>
    /// <param name="file">The CPCDS claim file (comma, pipe or tab delimited, header row first).</param>
    /// <param name="sourceSystem">The core that adjudicated the claims, e.g. <c>qnxt</c>.</param>
    /// <param name="extractedAt">When the core produced the extract (ISO 8601); freshness is measured from it.</param>
    /// <param name="lineOfBusiness">Commercial, Medicare, Medicaid, Exchange, TRICARE or VA. Default Commercial.</param>
    /// <param name="columnMap">JSON object: CPCDS column name → the file's header, for columns the file names differently.</param>
    /// <param name="dryRun">Parse, map and report only.</param>
    [HttpPost]
    [RequirePermission(ClaimsPermissions.Import)]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    [ProducesResponseType(typeof(CpcdsImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CpcdsImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CpcdsImportResult>> Import(
        [FromForm] IFormFile file,
        [FromForm] string? sourceSystem = null,
        [FromForm] DateTime? extractedAt = null,
        [FromForm] string? lineOfBusiness = null,
        [FromForm] string? columnMap = null,
        [FromForm] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty CPCDS claim file is required." });

        var lob = LineOfBusiness.Commercial;
        if (!string.IsNullOrWhiteSpace(lineOfBusiness)
            && (!Enum.TryParse(lineOfBusiness, ignoreCase: true, out lob) || !Enum.IsDefined(lob)))
        {
            return BadRequest(new { error = $"lineOfBusiness '{lineOfBusiness}' is not one of: {string.Join(", ", Enum.GetNames<LineOfBusiness>())}." });
        }

        Dictionary<string, string>? map = null;
        if (!string.IsNullOrWhiteSpace(columnMap))
        {
            try
            {
                map = JsonSerializer.Deserialize<Dictionary<string, string>>(columnMap);
            }
            catch (JsonException)
            {
                return BadRequest(new { error = "columnMap must be a JSON object of CPCDS column name to file header." });
            }
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream()))
        {
            content = await reader.ReadToEndAsync(ct);
        }

        var result = await _import.ImportAsync(
            TenantId,
            content,
            new CpcdsImportOptions
            {
                SourceSystem = string.IsNullOrWhiteSpace(sourceSystem) ? null : sourceSystem.Trim().ToLowerInvariant(),
                FileName = file.FileName,
                ExtractedAt = extractedAt?.ToUniversalTime(),
                LineOfBusiness = lob,
                ColumnMap = map,
                DryRun = dryRun,
            },
            _actor.UserId,
            ct);

        // A file the importer could not read at all is the sender's to fix;
        // a file with some rejected claims is a normal day, and the report says which.
        return result.FileErrors.Count > 0 && result.ClaimsInFile == 0
            ? UnprocessableEntity(result)
            : Ok(result);
    }
}
