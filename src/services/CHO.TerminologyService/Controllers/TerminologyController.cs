using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CHO.TerminologyService.Controllers;

/// <summary>
/// FHIR ConceptMap operations controller.
/// 
/// Primary endpoint: POST /fhir/ConceptMap/$translate
/// Follows the FHIR R4 ConceptMap/$translate operation specification.
/// 
/// Also exposes admin endpoints for map management and health checks.
///
/// Authentication (CHO tokens, shared layer):
///   - Every operation needs a CHO token except /health. Reads, including the POST
///     $translate and $batch-translate operations, need terminology:read; service
///     tokens satisfy it.
///   - Global data (NLM/AMA/SNOMED crosswalk maps and the built-in code-system catalog)
///     is shared by every tenant. Loading a global map needs platform:admin, which
///     tenant roles and service tokens never hold.
///   - Per-tenant data (plan overrides: map entries and versions with a TenantId) is
///     visible only to its tenant. Loading overrides needs settings:manage and always
///     writes to the token's tenant.
///   - The tenant is the token's. A tenantId in the query or a request body is a legacy
///     echo: it must equal the token tenant (403 otherwise) and is never used as the scope.
/// </summary>
[ApiController]
public class TerminologyController : ControllerBase
{
    private readonly ITerminologyTranslationService _translationService;
    private readonly IEnumerable<IMapLoader> _loaders;
    private readonly ILogger<TerminologyController> _logger;

    // Well-known coding system URIs
    public static class Systems
    {
        public const string SnomedCt = "http://snomed.info/sct";
        public const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";
        public const string Icd10 = "http://hl7.org/fhir/sid/icd-10";
        public const string Cpt = "http://www.ama-assn.org/go/cpt";
        public const string Hcpcs = "https://www.cms.gov/Medicare/Coding/HCPCSReleaseCodeSets";
    }

    /// <summary>Permission needed to load a global map (shared by every tenant).</summary>
    public const string GlobalWritePermission = "platform:admin";

    public TerminologyController(
        ITerminologyTranslationService translationService,
        IEnumerable<IMapLoader> loaders,
        ILogger<TerminologyController> logger)
    {
        _translationService = translationService;
        _loaders = loaders;
        _logger = logger;
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    /// <summary>
    /// A tenant named by the caller (query or body) is only an echo of the token tenant:
    /// null when it is absent or matches, otherwise a 403 result.
    /// </summary>
    private IActionResult? RejectForeignTenant(string? requestedTenantId, string tokenTenantId)
    {
        if (string.IsNullOrEmpty(requestedTenantId)
            || string.Equals(requestedTenantId, tokenTenantId, StringComparison.Ordinal))
            return null;

        return StatusCode(StatusCodes.Status403Forbidden,
            new { error = "tenantId must match the authenticated tenant" });
    }

    // ──────────────────────────────────────────────────────
    // FHIR $translate operation
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// FHIR ConceptMap/$translate - Translate a code from one system to another.
    /// 
    /// GET /fhir/ConceptMap/$translate?system={system}&amp;code={code}&amp;target={targetSystem}
    /// 
    /// Optional query params:
    ///   - tenantId: legacy; must equal the token tenant. Plan overrides are always the token tenant's.
    ///   - age: Patient age for context rules
    ///   - gender: Patient gender for context rules
    ///   - state: State code for TMPPM/Medicaid rules
    /// </summary>
    [HttpGet("fhir/ConceptMap/$translate")]
    [ProducesResponseType(typeof(TranslateResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> TranslateGet(
        [FromQuery] string system,
        [FromQuery] string code,
        [FromQuery] string target,
        [FromQuery] string? tenantId = null,
        [FromQuery] int? age = null,
        [FromQuery] string? gender = null,
        [FromQuery] string? state = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(target))
        {
            return BadRequest(new { error = "system, code, and target are required parameters" });
        }

        var tenant = HttpContext.GetTenantId();
        if (RejectForeignTenant(tenantId, tenant) is { } forbidden)
            return forbidden;

        var request = new TranslateRequest
        {
            System = system,
            Code = code,
            TargetSystem = target,
            TenantId = tenant,
            Context = (age.HasValue || gender != null || state != null)
                ? new PatientContext
                {
                    AgeInYears = age,
                    Gender = gender,
                    StateCode = state
                }
                : null
        };

        var response = await _translationService.TranslateAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// POST /fhir/ConceptMap/$translate - FHIR Parameters-based translate.
    /// Used by Da Vinci CRD/PAS servers that send structured requests.
    /// </summary>
    [HttpPost("fhir/ConceptMap/$translate")]
    [RequirePermission("terminology:read")] // a read, though POSTed
    [ProducesResponseType(typeof(TranslateResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> TranslatePost(
        [FromBody] TranslateRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.System) || string.IsNullOrEmpty(request.Code) ||
            string.IsNullOrEmpty(request.TargetSystem))
        {
            return BadRequest(new { error = "system, code, and targetSystem are required" });
        }

        var tenant = HttpContext.GetTenantId();
        if (RejectForeignTenant(request.TenantId, tenant) is { } forbidden)
            return forbidden;
        request.TenantId = tenant;

        var response = await _translationService.TranslateAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// POST /fhir/ConceptMap/$batch-translate - Batch translation for 278↔FHIR conversion.
    /// The PAS server sends multiple codes at once during a prior auth request conversion.
    /// </summary>
    [HttpPost("fhir/ConceptMap/$batch-translate")]
    [RequirePermission("terminology:read")] // a read, though POSTed
    [ProducesResponseType(typeof(List<TranslateResponse>), 200)]
    public async Task<IActionResult> BatchTranslate(
        [FromBody] List<TranslateRequest> requests,
        CancellationToken ct = default)
    {
        if (requests == null || requests.Count == 0)
        {
            return BadRequest(new { error = "At least one translate request is required" });
        }

        if (requests.Count > 500)
        {
            return BadRequest(new { error = "Maximum 500 codes per batch" });
        }

        var tenant = HttpContext.GetTenantId();
        foreach (var request in requests)
        {
            if (request is null)
                return BadRequest(new { error = "Translate requests must not be null" });
            if (RejectForeignTenant(request.TenantId, tenant) is { } forbidden)
                return forbidden;
            request.TenantId = tenant;
        }

        var responses = await _translationService.BatchTranslateAsync(requests, ct);
        return Ok(responses);
    }

    /// <summary>
    /// GET /fhir/CodeSystem/$lookup - Look up display metadata for a code.
    ///
    /// This intentionally returns a compact CHO payload rather than the full
    /// FHIR Parameters resource so service consumers can cheaply enrich UI
    /// display fields without taking a dependency on full terminology maps.
    /// </summary>
    [HttpGet("fhir/CodeSystem/$lookup")]
    [ProducesResponseType(typeof(CodeLookupResponse), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> LookupCodeGet(
        [FromQuery] string system,
        [FromQuery] string code,
        [FromQuery] string? tenantId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(code))
        {
            return BadRequest(new { error = "system and code are required parameters" });
        }

        var tenant = HttpContext.GetTenantId();
        if (RejectForeignTenant(tenantId, tenant) is { } forbidden)
            return forbidden;

        var response = await _translationService.LookupCodeAsync(new CodeLookupRequest
        {
            System = system,
            Code = code,
            TenantId = tenant
        }, ct);

        return Ok(response);
    }

    // ──────────────────────────────────────────────────────
    // Admin: Map management
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// GET /admin/maps - List the map versions the caller's tenant can use:
    /// the global maps and its own override versions (never another tenant's).
    /// Shows active/inactive versions with entry counts and import timestamps.
    /// </summary>
    [HttpGet("admin/maps")]
    [ProducesResponseType(typeof(List<MapVersion>), 200)]
    public async Task<IActionResult> GetMapVersions(CancellationToken ct = default)
    {
        var versions = await _translationService.GetMapVersionsAsync(HttpContext.GetTenantId(), ct);
        return Ok(versions);
    }

    /// <summary>
    /// POST /admin/maps/load - Load a crosswalk map file.
    /// Accepts RF2 (NLM SNOMED maps) or CSV (AMA cross maps, plan overrides).
    ///
    /// Scope:
    ///   - isOverride=true: the token tenant's plan overrides. settings:manage (default write).
    ///   - isOverride=false: a global map every tenant translates with (it also
    ///     deactivates earlier versions of the same map name). Needs platform:admin.
    /// 
    /// Query params:
    ///   - format: "RF2" or "CSV"
    ///   - mapName: Identifier for this map
    ///   - version: Version string from source
    ///   - sourceSystem: Source coding system URI
    ///   - targetSystem: Target coding system URI
    ///   - tenantId: (optional, legacy) must equal the token tenant; overrides always go to the token tenant
    ///   - isOverride: (optional) true if this is a plan override file
    /// </summary>
    [HttpPost("admin/maps/load")]
    [ProducesResponseType(typeof(MapLoadResult), 200)]
    [ProducesResponseType(400)]
    [RequestSizeLimit(500_000_000)] // 500MB for large RF2 files
    public async Task<IActionResult> LoadMap(
        [FromQuery] string format,
        [FromQuery] string mapName,
        [FromQuery] string version,
        [FromQuery] string sourceSystem,
        [FromQuery] string targetSystem,
        [FromQuery] string? tenantId = null,
        [FromQuery] bool isOverride = false,
        CancellationToken ct = default)
    {
        // CHO permissions replace the former optional X-Admin-Key check (open when unset).
        var tenant = HttpContext.GetTenantId();
        if (RejectForeignTenant(tenantId, tenant) is { } forbidden)
            return forbidden;

        if (!isOverride)
        {
            // Non-override entries are read by every tenant's translations whatever
            // TenantId they carry, so a non-override load is global and cannot be
            // scoped to a tenant.
            if (!string.IsNullOrEmpty(tenantId))
                return BadRequest(new { error = "Only override maps (isOverride=true) are tenant-scoped; omit tenantId for a global map" });

            if (!ChoPrincipal.HasPermission(User, GlobalWritePermission))
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { error = $"Global maps are shared by every tenant; loading one needs {GlobalWritePermission}" });
        }

        var actor = User.FindFirst(ChoClaimTypes.Subject)?.Value;

        if (Request.Body == null)
        {
            return BadRequest(new { error = "Request body must contain the map file" });
        }

        var loader = _loaders.FirstOrDefault(l =>
            l.Format.Equals(format, StringComparison.OrdinalIgnoreCase));

        if (loader == null)
        {
            return BadRequest(new { error = $"Unsupported format: {format}. Supported: RF2, CSV" });
        }

        var options = new MapLoadOptions
        {
            MapName = mapName,
            Version = version,
            SourceSystem = sourceSystem,
            TargetSystem = targetSystem,
            TenantId = isOverride ? tenant : null,
            IsOverride = isOverride,
            ImportedBy = actor
        };

        _logger.LogInformation("Loading map: {MapName} v{Version} ({Format}) {Source} → {Target}",
            SanitizeForLog(mapName), SanitizeForLog(version), SanitizeForLog(format), SanitizeForLog(sourceSystem), SanitizeForLog(targetSystem));

        var result = await loader.LoadAsync(Request.Body, options, ct);
        _logger.LogInformation(
            "AUDIT terminology map load: {MapName} v{Version} ({Scope}) by {Subject} in tenant {TenantId}: success {Success}, {Entries} entries, version {MapVersionId}",
            SanitizeForLog(mapName), SanitizeForLog(version), isOverride ? "tenant override" : "global",
            SanitizeForLog(actor), SanitizeForLog(tenant), result.Success, result.EntriesLoaded, SanitizeForLog(result.MapVersionId));
        return Ok(result);
    }

    // ──────────────────────────────────────────────────────
    // Health check
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// GET /health - Service health check. Anonymous: Kubernetes probes and the
    /// portal's status card call it without a token. It lists global maps only
    /// (never a tenant's override versions) and no exception text.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct = default)
    {
        try
        {
            var versions = await _translationService.GetMapVersionsAsync(tenantId: null, ct);
            var activeVersions = versions.Where(v => v.IsActive).ToList();

            return Ok(new
            {
                status = "healthy",
                service = "CHO.TerminologyService",
                activeMaps = activeVersions.Select(v => new
                {
                    v.MapName,
                    v.Version,
                    v.SourceSystem,
                    v.TargetSystem,
                    v.EntryCount,
                    v.ImportedAt
                }),
                totalActiveEntries = activeVersions.Sum(v => v.EntryCount),
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            return StatusCode(503, new
            {
                status = "unhealthy",
                timestamp = DateTime.UtcNow
            });
        }
    }
}
