using System.Text.Json;
using AuthorizationService.Import;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AuthorizationService.Controllers;

/// <summary>
/// Loads prior authorizations another system decided (the plan's UM platform
/// or core) so CHO's CMS-0057-F APIs serve them. Imported authorizations are
/// read-only in CHO. See <c>docs/integration/AUTHORIZATION-IMPORT.md</c>.
/// </summary>
[ApiController]
[Route("api/v1/authorizations/import")]
[Produces("application/json")]
public class AuthorizationImportController : ControllerBase
{
    /// <summary>Load authorizations from another system: tenant administrators (<c>*:*</c>) and CHO services (the file drop).</summary>
    public const string ImportPermission = "authorizations:import";

    private readonly IAuthorizationImportService _import;
    private readonly ICurrentActor _actor;

    public AuthorizationImportController(IAuthorizationImportService import, ICurrentActor actor)
    {
        _import = import;
        _actor = actor;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    /// <summary>
    /// Loads one authorization CSV. Reloading is safe: unchanged
    /// authorizations are left alone and changed ones are updated. Set
    /// <c>dryRun</c> to see the report without writing anything.
    /// </summary>
    /// <param name="file">The CSV (comma, pipe or tab delimited, header row first), one row per service line.</param>
    /// <param name="sourceSystem">The system that decided the authorizations, e.g. <c>jiva</c>.</param>
    /// <param name="extractedAt">When the source produced the extract (ISO 8601).</param>
    /// <param name="lineOfBusiness">For rows with no line_of_business: Commercial, Medicare, Medicaid, Exchange, TRICARE or VA. Default Commercial.</param>
    /// <param name="columnMap">JSON object: documented column header → the file's header, for columns the file names differently.</param>
    /// <param name="dryRun">Parse, map and report only.</param>
    [HttpPost("csv")]
    [RequirePermission(ImportPermission)]
    [RequestSizeLimit(200_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
    [ProducesResponseType(typeof(AuthorizationImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthorizationImportResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthorizationImportResult>> ImportCsv(
        [FromForm] IFormFile file,
        [FromForm] string? sourceSystem = null,
        [FromForm] DateTime? extractedAt = null,
        [FromForm] string? lineOfBusiness = null,
        [FromForm] string? columnMap = null,
        [FromForm] bool dryRun = false,
        CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty authorization file is required." });

        var lob = LineOfBusiness.Commercial;
        if (!string.IsNullOrWhiteSpace(lineOfBusiness)
            && (!Enum.TryParse(lineOfBusiness, ignoreCase: true, out lob) || !Enum.IsDefined(lob) || lob == LineOfBusiness.Unknown))
        {
            return BadRequest(new { error = $"lineOfBusiness '{lineOfBusiness}' is not one of: Commercial, Medicare, Medicaid, Exchange, TRICARE, VA." });
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
                return BadRequest(new { error = "columnMap must be a JSON object of column header to the file's header." });
            }
        }

        string content;
        using (var reader = new StreamReader(file.OpenReadStream()))
            content = await reader.ReadToEndAsync(ct);

        var result = await _import.ImportCsvAsync(
            TenantId,
            content,
            new AuthorizationImportOptions
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

        return result.FileErrors.Count > 0 && result.AuthorizationsInFile == 0
            ? UnprocessableEntity(result)
            : Ok(result);
    }
}
