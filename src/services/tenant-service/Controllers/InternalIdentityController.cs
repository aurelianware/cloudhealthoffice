using Microsoft.AspNetCore.Mvc;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Identity lookups for token-service, which exchanges a user's Entra token for
/// a CHO token and must resolve the user's tenant membership itself.
///
/// SECURITY: these endpoints reveal who belongs to which tenant and can link an
/// Entra identity to a user. tenant-service has no authentication yet. When it
/// is moved onto AddChoAuthentication, every action here MUST be restricted to
/// the token-service service identity (a service token from the internal issuer
/// with sub/azp = "token-service" and the cho.service role) and refused to every
/// other caller, users included. The lookups are cross-tenant by nature, so that
/// check must not depend on the token's tenant claim. Until then, keep
/// tenant-service unreachable from outside the cluster.
/// </summary>
[ApiController]
[Route("internal/v1/identity")]
public class InternalIdentityController : ControllerBase
{
    private readonly IIdentityDirectory _directory;

    public InternalIdentityController(IIdentityDirectory directory) => _directory = directory;

    /// <summary>TenantUsers linked to Entra object <paramref name="oid"/> in directory <paramref name="tid"/>.</summary>
    // TODO(tenant-service auth): restrict to the token-service service identity.
    [HttpGet("memberships")]
    public async Task<ActionResult<IReadOnlyList<IdentityMembership>>> GetMemberships(
        [FromQuery] string tid, [FromQuery] string oid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tid) || string.IsNullOrWhiteSpace(oid))
            return BadRequest(new { error = "tid and oid are required" });
        return Ok(await _directory.GetMembershipsAsync(tid, oid, ct));
    }

    /// <summary>All tenants, or those registered to Entra directory <paramref name="azureTenantId"/>.</summary>
    // TODO(tenant-service auth): restrict to the token-service service identity.
    [HttpGet("tenants")]
    public async Task<ActionResult<IReadOnlyList<IdentityTenant>>> GetTenants(
        [FromQuery] string? azureTenantId, CancellationToken ct)
        => Ok(await _directory.GetTenantsAsync(azureTenantId, ct));

    // TODO(tenant-service auth): restrict to the token-service service identity.
    [HttpGet("tenants/{tenantId}")]
    public async Task<ActionResult<IdentityTenant>> GetTenant(string tenantId, CancellationToken ct)
    {
        var tenant = await _directory.GetTenantAsync(tenantId, ct);
        return tenant == null ? NotFound() : Ok(tenant);
    }

    /// <summary>
    /// One user in one tenant by email. POST so the address travels in the body
    /// and stays out of URLs and access logs.
    /// </summary>
    // TODO(tenant-service auth): restrict to the token-service service identity.
    [HttpPost("tenants/{tenantId}/users/find-by-email")]
    public async Task<ActionResult<IdentityUser>> FindByEmail(
        string tenantId, [FromBody] FindByEmailRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
            return BadRequest(new { error = "email is required" });
        var user = await _directory.FindUserByEmailAsync(tenantId, request.Email, ct);
        return user == null ? NotFound() : Ok(user);
    }

    /// <summary>
    /// Records an Entra oid+tid on a user that has no link yet. Refuses (409) to
    /// replace an existing, different link.
    /// </summary>
    // TODO(tenant-service auth): restrict to the token-service service identity.
    [HttpPost("tenants/{tenantId}/users/{userId}/entra-link")]
    public async Task<ActionResult<IdentityUser>> Link(
        string tenantId, string userId, [FromBody] EntraLinkRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AzureAdObjectId) || string.IsNullOrWhiteSpace(request.AzureAdTenantId))
            return BadRequest(new { error = "azureAdObjectId and azureAdTenantId are required" });

        var (outcome, user) = await _directory.LinkEntraIdentityAsync(
            tenantId, userId, request.AzureAdObjectId, request.AzureAdTenantId, ct);
        return outcome switch
        {
            LinkOutcome.Linked => Ok(user),
            LinkOutcome.NotFound => NotFound(),
            _ => Conflict(new { error = "already_linked" }),
        };
    }
}

public sealed class FindByEmailRequest
{
    public string Email { get; set; } = string.Empty;
}

public sealed class EntraLinkRequest
{
    public string AzureAdObjectId { get; set; } = string.Empty;
    public string AzureAdTenantId { get; set; } = string.Empty;
}
