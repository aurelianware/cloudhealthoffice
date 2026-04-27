using Microsoft.AspNetCore.Mvc;
using ProviderService.Adapters;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace ProviderService.Controllers;

/// <summary>
/// REST surface for payer-defined provider networks (the
/// <see cref="Organization"/> entity from capability 5.3). Reads route
/// through <see cref="IOrganizationAdapter"/> per tenant config; writes
/// go directly to <see cref="IOrganizationService"/> on the CHO store.
/// </summary>
[ApiController]
[Route("api/v1/networks")]
[Produces("application/json")]
public class NetworksController : ControllerBase
{
    private readonly IOrganizationService _service;
    private readonly OrganizationAdapterFactory _adapterFactory;
    private readonly ILogger<NetworksController> _logger;

    public NetworksController(
        IOrganizationService service,
        OrganizationAdapterFactory adapterFactory,
        ILogger<NetworksController> logger)
    {
        _service = service;
        _adapterFactory = adapterFactory;
        _logger = logger;
    }

    private string TenantId =>
        HttpContext.Items["TenantId"]?.ToString()
            ?? throw new InvalidOperationException("TenantId not found in request context");

    /// <summary>
    /// Paginated list of networks. Filters are AND-combined.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(NetworkListResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<NetworkListResponse>> List(
        [FromQuery] NetworkType? networkType = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string? parentOrganizationId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page <= 0) page = 1;
        if (pageSize <= 0 || pageSize > 200) pageSize = 50;

        _logger.LogInformation(
            "Listing networks: networkType={NetworkType}, lob={LOB}, parent={Parent}",
            networkType, lineOfBusiness, SanitizeForLog(parentOrganizationId));

        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.ListAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            NetworkType = networkType,
            LineOfBusiness = lineOfBusiness,
            ParentOrganizationId = parentOrganizationId,
            Page = page,
            PageSize = pageSize,
            PlatformSettings = settings,
        });

        return Ok(new NetworkListResponse
        {
            Items = response.Organizations.Select(o => o.ToOrganization()).ToList(),
            TotalCount = response.TotalCount,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>Get a single network by id.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Organization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Organization>> GetById(string id)
    {
        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.GetOrganizationAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            OrganizationId = id,
            PlatformSettings = settings,
        });

        if (response.Organization == null)
        {
            return NotFound(new { message = $"Network {id} not found" });
        }

        return Ok(response.Organization.ToOrganization());
    }

    /// <summary>Get child networks for a parent (partOf hierarchy traversal).</summary>
    [HttpGet("{id}/children")]
    [ProducesResponseType(typeof(IEnumerable<Organization>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Organization>>> GetChildren(string id)
    {
        var (adapter, settings) = await _adapterFactory.GetAdapterWithSettingsAsync(TenantId);
        var response = await adapter.GetByParentAsync(new OrganizationAdapterRequest
        {
            TenantId = TenantId,
            ParentOrganizationId = id,
            PlatformSettings = settings,
        });

        return Ok(response.Organizations.Select(o => o.ToOrganization()));
    }

    /// <summary>Create a new network. Activates v1 in one shot.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(Organization), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Organization>> Create([FromBody] Organization organization)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        organization.TenantId = TenantId;
        var created = await _service.CreateAndActivateAsync(organization, ResolveActorId());
        return CreatedAtAction(nameof(GetById), new { id = created.OrganizationId }, created);
    }

    /// <summary>
    /// Update a network. Internally clones the head into a new Active
    /// version and supersedes the prior one — the chain stays intact and
    /// addressable under the same <see cref="Organization.OrganizationId"/>.
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Organization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Organization>> Update(string id, [FromBody] Organization organization)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            organization.TenantId = TenantId;
            var updated = await _service.UpdateAsync(id, organization, ResolveActorId());
            return Ok(updated);
        }
        catch (OrganizationVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message, organizationId = ex.OrganizationId });
        }
        catch (OrganizationVersionStateException ex)
        {
            return Conflict(new
            {
                message = ex.Message,
                organizationId = ex.OrganizationId,
                versionId = ex.VersionId,
                versionState = ex.CurrentState.ToString()
            });
        }
    }

    /// <summary>Soft-delete the network by terminating the current head version.</summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, [FromQuery] string? reason = null)
    {
        try
        {
            await _service.TerminateAsync(id, reason ?? "deleted via DELETE /api/v1/networks", ResolveActorId());
            return NoContent();
        }
        catch (OrganizationVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    private string ResolveActorId()
    {
        var sub = HttpContext.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrEmpty(sub)) return sub;
        if (HttpContext.Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrEmpty(header.ToString()))
            return header.ToString();
        return "system";
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

/// <summary>Page envelope for the list endpoint.</summary>
public sealed class NetworkListResponse
{
    public IReadOnlyList<Organization> Items { get; set; } = Array.Empty<Organization>();
    public int? TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
