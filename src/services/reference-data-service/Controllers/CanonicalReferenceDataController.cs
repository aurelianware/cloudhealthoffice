using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using CloudHealthOffice.ReferenceData.Domain;
using CloudHealthOffice.ReferenceData.Persistence;
using CloudHealthOffice.ReferenceData.Security;
using CloudHealthOffice.ReferenceData.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ReferenceDataService.Controllers;

[ApiController]
[Route("api/reference-data/codes")]
[Produces("application/json")]
public sealed class CanonicalReferenceDataController : ControllerBase
{
    private readonly CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository _repository;

    public CanonicalReferenceDataController(
        CloudHealthOffice.ReferenceData.Persistence.IReferenceDataRepository repository)
    {
        _repository = repository;
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
    [Authorize(Policy = "AdminPolicy")]
    [ProducesResponseType(typeof(ImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImportResult>> Import(
        [FromBody] IReadOnlyList<ReferenceCode> records,
        CancellationToken ct = default)
    {
        if (records.Count == 0)
            return BadRequest(new { message = "At least one reference record is required." });

        var tenantId = ResolveTenantId();
        if (records.Any(record => record.TenantId is not null
                && !string.Equals(record.TenantId, tenantId, StringComparison.Ordinal)))
            return Forbid();

        try
        {
            return Ok(await _repository.ImportAsync(records, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private ReferenceDataAccessContext CreateAccessContext()
    {
        var authenticated = User.Identity?.IsAuthenticated == true;
        return new ReferenceDataAccessContext(
            authenticated,
            authenticated ? ResolveTenantId() : null,
            authenticated && (User.IsInRole("Administrator") || User.HasClaim("cho_internal", "true")));
    }

    private string? ResolveTenantId()
    {
        var tenantId = User.FindFirstValue("tenant_id")
            ?? User.FindFirstValue("extension_TenantId")
            ?? User.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");

        if (string.IsNullOrWhiteSpace(tenantId) && User.Identity?.IsAuthenticated == true)
            tenantId = Request.Headers["X-Tenant-ID"].FirstOrDefault();

        return string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
    }
}
