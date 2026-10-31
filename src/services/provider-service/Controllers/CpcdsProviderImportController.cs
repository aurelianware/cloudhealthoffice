using System.Text.Json;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.AspNetCore.Mvc;
using ProviderService.Security;
using ProviderService.Services.Cpcds;

namespace ProviderService.Controllers;

/// <summary>
/// The providers a core's CPCDS claim extract names, so the EOBs claims-service
/// builds from it (docs/integration/CPCDS-FILE-DROP-IMPORTER.md) reference
/// Practitioners and Organizations CHO can serve. Send the same claim file
/// that goes to <c>POST api/v1/claims/import/cpcds</c>.
/// </summary>
[ApiController]
[Route("api/v1/providers/import/cpcds-claims")]
[Produces("application/json")]
public class CpcdsProviderImportController : ControllerBase
{
    private const long MaxUploadBytes = 200L * 1024 * 1024;

    private readonly CpcdsReferencedProviderImporter _importer;
    private readonly ILogger<CpcdsProviderImportController> _logger;

    public CpcdsProviderImportController(
        CpcdsReferencedProviderImporter importer, ILogger<CpcdsProviderImportController> logger)
    {
        _importer = importer;
        _logger = logger;
    }

    /// <summary>
    /// Creates a reference-only provider, described from NPPES, for every NPI
    /// in the file's provider columns that CHO does not already have. Existing
    /// providers are left alone. Reference-only providers are readable through
    /// the FHIR API but never published in the public Provider Directory.
    /// <c>dryRun=true</c> reports what would be created.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [ProducesResponseType(typeof(CpcdsProviderImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CpcdsProviderImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CpcdsProviderImportResult>> Import(
        [FromForm] IFormFile file,
        [FromForm] string? columnMap = null,
        [FromForm] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty CPCDS claim file is required." });

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
            content = await reader.ReadToEndAsync(ct);

        var tenantId = this.TokenTenantId();
        var result = await _importer.ImportAsync(tenantId, content, map, dryRun, this.TokenActorId(), ct);
        _logger.LogInformation(
            "CPCDS claim providers {FileName} for tenant {TenantId}: {Npis} NPIs, {Created} created, {Known} known, {NotInNppes} not in NPPES, {Failed} lookups failed (dry run {DryRun})",
            LogSanitizer.Sanitize(file.FileName), LogSanitizer.Sanitize(tenantId),
            result.NpisInFile, result.Created, result.AlreadyKnown, result.NotInNppes, result.LookupFailed, dryRun);

        return result.FileErrors.Count > 0 ? UnprocessableEntity(result) : Ok(result);
    }
}
