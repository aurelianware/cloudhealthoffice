using System.Globalization;
using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services.CodeSets;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace CHO.TerminologyService.Controllers;

/// <summary>
/// Dated code sets: "was this ICD-10-CM, ICD-10-PCS or HCPCS code valid on this
/// date of service?" Built from the public-domain CMS release files.
///
/// <list type="bullet">
///   <item><c>GET /fhir/CodeSystem/$validate-code?url=&amp;code=&amp;date=</c> and
///   <c>POST /fhir/CodeSystem/$batch-validate-code</c>: terminology:read (service
///   tokens satisfy it). A compact answer, like <c>$lookup</c>.</item>
///   <item><c>POST /admin/code-sets/load</c>: platform:admin, since every tenant reads the result.</item>
///   <item><c>GET /admin/code-sets</c>: terminology:read; lists the loaded releases.</item>
/// </list>
/// </summary>
[ApiController]
public class CodeSetController : ControllerBase
{
    private readonly CodeSetValidationService _service;
    private readonly ICodeSetRepository _repository;
    private readonly ILogger<CodeSetController> _logger;

    public CodeSetController(
        CodeSetValidationService service, ICodeSetRepository repository, ILogger<CodeSetController> logger)
    {
        _service = service;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Validates one code for a date of service. <c>url</c> (FHIR's name) or
    /// <c>system</c> names the code system; <c>date</c> is yyyy-MM-dd and defaults
    /// to today. <c>status</c> is valid, invalid or unknown; unknown (no release
    /// loaded for that date or system) must not be read as invalid.
    /// </summary>
    [HttpGet("fhir/CodeSystem/$validate-code")]
    [ProducesResponseType(typeof(CodeValidityResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> ValidateCode(
        [FromQuery] string? url, [FromQuery] string? system, [FromQuery] string? code, [FromQuery] string? date,
        CancellationToken ct = default)
    {
        var codeSystem = string.IsNullOrWhiteSpace(url) ? system : url;
        if (string.IsNullOrWhiteSpace(codeSystem) || string.IsNullOrWhiteSpace(code))
            return BadRequest(new { error = "url (or system) and code are required" });

        DateOnly? day = null;
        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return BadRequest(new { error = "date must be yyyy-MM-dd" });
            day = parsed;
        }

        return Ok(await _service.ValidateAsync(new CodeValidityRequest { System = codeSystem, Code = code, Date = day }, ct));
    }

    /// <summary>Validates many codes at once (a claim's diagnoses and lines). Answers are in request order.</summary>
    [HttpPost("fhir/CodeSystem/$batch-validate-code")]
    [RequirePermission("terminology:read")] // a read, though POSTed
    [ProducesResponseType(typeof(List<CodeValidityResponse>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> BatchValidateCode(
        [FromBody] List<CodeValidityRequest> requests, CancellationToken ct = default)
    {
        if (requests is null || requests.Count == 0)
            return BadRequest(new { error = "At least one code is required" });
        if (requests.Count > CodeSetValidationService.MaxBatch)
            return BadRequest(new { error = $"At most {CodeSetValidationService.MaxBatch} codes per request" });
        if (requests.Any(r => r is null || string.IsNullOrWhiteSpace(r.System) || string.IsNullOrWhiteSpace(r.Code)))
            return BadRequest(new { error = "Every entry needs system and code" });

        return Ok(await _service.ValidateManyAsync(requests, ct));
    }

    /// <summary>
    /// Loads a CMS release. Query: <c>system</c> (ICD-10-CM, ICD-10-PCS or HCPCS
    /// URI), <c>format</c> (ICD10-ORDER or HCPCS-CSV), <c>version</c> (FY2026,
    /// 2026-Q2), optional <c>effectiveFrom</c>/<c>effectiveTo</c> (yyyy-MM-dd).
    /// The body is the file. Global data: platform:admin.
    /// </summary>
    [HttpPost("admin/code-sets/load")]
    [RequirePermission(TerminologyController.GlobalWritePermission)]
    [RequestSizeLimit(200_000_000)]
    [ProducesResponseType(typeof(CodeSetLoadResult), 200)]
    [ProducesResponseType(typeof(CodeSetLoadResult), 400)]
    public async Task<IActionResult> Load(
        [FromQuery] string system, [FromQuery] string format, [FromQuery] string version,
        [FromQuery] string? effectiveFrom = null, [FromQuery] string? effectiveTo = null,
        CancellationToken ct = default)
    {
        if (!TryDay(effectiveFrom, out var from) || !TryDay(effectiveTo, out var to))
            return BadRequest(new { error = "effectiveFrom and effectiveTo must be yyyy-MM-dd" });

        var actor = User.FindFirst(ChoClaimTypes.Subject)?.Value;
        var result = await _service.LoadAsync(new CodeSetLoadRequest
        {
            System = system,
            Format = format ?? string.Empty,
            Version = version,
            EffectiveFrom = from,
            EffectiveTo = to,
            LoadedBy = actor,
        }, Request.Body, ct);

        _logger.LogInformation(
            "AUDIT code set load: {System} {Version} ({Format}) by {Subject}: success {Success}, {Codes} codes",
            LogSanitizer.Sanitize(system), LogSanitizer.Sanitize(version), LogSanitizer.Sanitize(format),
            LogSanitizer.Sanitize(actor), result.Success, result.CodesLoaded);

        return result.Success ? Ok(result) : BadRequest(result);
    }

    /// <summary>The loaded releases and the dates each one covers.</summary>
    [HttpGet("admin/code-sets")]
    [ProducesResponseType(typeof(List<CodeSetRelease>), 200)]
    public async Task<IActionResult> Releases(CancellationToken ct = default)
        => Ok(await _repository.GetReleasesAsync(null, ct));

    private static bool TryDay(string? value, out DateOnly? day)
    {
        day = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        day = parsed;
        return true;
    }
}
