using System.ComponentModel.DataAnnotations;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Persistence;
using CloudHealthOffice.ReferenceData.Security;
using CloudHealthOffice.ReferenceData.Sources;
using Microsoft.AspNetCore.Mvc;

namespace ReferenceDataService.Controllers;

/// <summary>
/// Canonical code sets. A record with no <c>TenantId</c> is global: one copy is
/// shared by every tenant. A record with a <c>TenantId</c> belongs to that tenant
/// and is visible only to it.
///
/// Reads need reference-data:read (default). The tenant used to scope a read is
/// the token's tenant, never a header. Imports need settings:manage (default) and
/// may only carry records for the token's tenant; a batch with any global record
/// changes data every tenant sees, so it also needs platform:admin, which tenant
/// roles and service tokens never hold. Idempotency ("already imported") is per
/// tenant (global batches are their own scope) and the import ledger records the
/// token subject.
/// </summary>
[ApiController]
[Route("api/reference-data/codes")]
[Produces("application/json")]
public sealed class CanonicalReferenceDataController : ControllerBase
{
    private readonly CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository _repository;
    private readonly ILogger<CanonicalReferenceDataController>? _logger;

    public CanonicalReferenceDataController(
        CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository repository,
        ILogger<CanonicalReferenceDataController>? logger = null)
    {
        _repository = repository;
        _logger = logger;
    }

    [HttpGet("{codeSystem}/{code}")]
    [ProducesResponseType(typeof(ReferenceCode), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReferenceCode>> Get(
        string codeSystem,
        string code,
        [FromQuery] DateOnly? effectiveDate = null,
        [FromQuery] string? version = null,
        CancellationToken ct = default)
    {
        var access = CreateAccessContext();
        var result = await _repository.GetAsync(
            codeSystem,
            code,
            effectiveDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            version,
            access.TenantId,
            ct);

        return result is null ? NotFound() : Ok(ReferenceDataExposurePolicy.Redact(result, access));
    }

    [HttpGet]
    [ProducesResponseType(typeof(Page<ReferenceCode>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Page<ReferenceCode>>> Search(
        [FromQuery] string codeSystem,
        [FromQuery] string? search = null,
        [FromQuery] ReferenceSearchMode searchMode = ReferenceSearchMode.Exact,
        [FromQuery] string? category = null,
        [FromQuery] string? version = null,
        [FromQuery] DateOnly? effectiveDate = null,
        [FromQuery] bool? active = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 500)] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(codeSystem))
            return BadRequest(new { message = "codeSystem is required." });

        var access = CreateAccessContext();
        var result = await _repository.SearchAsync(new ReferenceDataQuery
        {
            CodeSystem = codeSystem,
            Search = search,
            SearchMode = searchMode,
            Category = category,
            Version = version,
            EffectiveDate = effectiveDate,
            Active = active,
            TenantId = access.TenantId,
            Page = page,
            PageSize = pageSize
        }, ct);

        return Ok(result with
        {
            Items = result.Items.Select(item => ReferenceDataExposurePolicy.Redact(item, access)).ToList()
        });
    }

    [HttpPost("import")]
    [ProducesResponseType(typeof(ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResult>> Import(
        [FromBody] IReadOnlyList<ReferenceCode> records,
        CancellationToken ct = default)
    {
        if (records.Count == 0)
            return BadRequest(new { message = "At least one reference record is required." });

        var tenantId = HttpContext.GetTenantId();
        if (records.Any(record => record.TenantId is not null
                && !string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Records can only be imported for the authenticated tenant." });

        var global = records.Any(record => record.TenantId is null);
        if (global && !ChoPrincipal.HasPermission(User, GlobalWritePermission))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = $"Global reference data is shared by every tenant; importing it needs {GlobalWritePermission}." });

        // "Already imported" is decided within the batch's scope (the token
        // tenant, or global), so the answer never reflects another tenant's imports.
        // The ledger's actor is the token subject, never a body value.
        var subject = User.FindFirst(ChoClaimTypes.Subject)?.Value;
        try
        {
            var result = await _repository.ImportAsync(records, subject, ct);
            _logger?.LogInformation(
                "AUDIT reference-data import: {Count} {Scope} records (source {SourceId} {SourceVersion}) by {Subject} in tenant {TenantId}; already imported: {AlreadyImported}",
                result.ImportedCount, global ? "global" : "tenant", Sanitize(records[0].SourceId), Sanitize(records[0].SourceVersion),
                Sanitize(subject), Sanitize(tenantId), result.AlreadyImported);
            return Ok(result);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    /// <summary>Permission needed to write global (cross-tenant) reference data.</summary>
    public const string GlobalWritePermission = "platform:admin";

    /// <summary>
    /// Who is reading: the tenant comes from the validated token (set by the
    /// shared tenant middleware), never from a header. InternalOnly records are
    /// readable by service tokens and platform administrators.
    /// </summary>
    private ReferenceDataAccessContext CreateAccessContext()
    {
        if (User.Identity?.IsAuthenticated != true)
            return new ReferenceDataAccessContext(false);

        return new ReferenceDataAccessContext(
            true,
            HttpContext.GetTenantId(),
            ChoPrincipal.IsService(User) || ChoPrincipal.HasPermission(User, GlobalWritePermission));
    }

    private static string Sanitize(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
