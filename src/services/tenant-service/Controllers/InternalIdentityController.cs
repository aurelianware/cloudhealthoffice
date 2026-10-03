using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Identity lookups for token-service, which exchanges a user's Entra token for
/// a CHO token and must resolve the user's tenant membership itself.
///
/// SECURITY: these endpoints reveal who belongs to which tenant and can link an
/// Entra identity to a user. Every action is restricted to the token-service
/// service identity: a service token from an issuer trusted to mint service
/// tokens, with <c>sub</c> = <c>azp</c> = <c>token-service</c>. User tokens
/// (platform administrators included) and every other service get 403.
///
/// The lookups are cross-tenant by nature. token-service names
/// <c>cho-platform</c> as the tenant for cross-tenant calls and the looked-up
/// tenant for per-tenant calls, so the check is on the caller's identity, never
/// on that tenant value: the route tenant check is skipped here, and only here.
/// </summary>
[ApiController]
[Route("internal/v1/identity")]
[RequireServiceClient(TokenServiceClientId)]
[SkipRouteTenantCheck]
public class InternalIdentityController : ControllerBase
{
    private readonly IIdentityDirectory _directory;
    private readonly IInvitationStore _invitations;

    /// <summary>The only identity allowed to call these endpoints.</summary>
    public const string TokenServiceClientId = "token-service";

    public InternalIdentityController(IIdentityDirectory directory, IInvitationStore invitations)
    {
        _directory = directory;
        _invitations = invitations;
    }

    /// <summary>
    /// Redeems an invitation for the Entra identity token-service validated:
    /// links <c>tid</c>+<c>oid</c> to the invited user and activates it, in one
    /// conditional write (see <see cref="IInvitationStore"/>). The signed-in
    /// <c>email</c> must be the invited address. Answers 200 <c>{ tenantId, userId }</c>,
    /// or <c>{ error }</c>: 404 not_found, 410 expired / revoked, 409
    /// already_redeemed / identity_in_use, 403 email_mismatch (with the invited
    /// address masked). A wrong code reveals nothing about any invitation.
    /// </summary>
    [HttpPost("invitations/redeem")]
    public async Task<IActionResult> RedeemInvitation([FromBody] RedeemInvitationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Tid) || string.IsNullOrWhiteSpace(request.Oid))
            return BadRequest(new { error = "invalid_request" });
        if (!InvitationCodes.IsWellFormed(request.Code))
            return NotFound(new { error = "not_found" });

        var result = await _invitations.RedeemAsync(
            InvitationCodes.Hash(request.Code), request.Tid.Trim(), request.Oid.Trim(), request.Email ?? string.Empty, ct);

        return result.Error switch
        {
            InvitationError.None => Ok(new { tenantId = result.TenantId, userId = result.UserId }),
            InvitationError.NotFound => NotFound(new { error = "not_found" }),
            InvitationError.Expired => StatusCode(StatusCodes.Status410Gone, new { error = "expired" }),
            InvitationError.Revoked => StatusCode(StatusCodes.Status410Gone, new { error = "revoked" }),
            InvitationError.AlreadyRedeemed => Conflict(new { error = "already_redeemed" }),
            InvitationError.IdentityInUse => Conflict(new { error = "identity_in_use" }),
            InvitationError.EmailMismatch => StatusCode(StatusCodes.Status403Forbidden,
                new { error = "email_mismatch", invitedEmail = result.MaskedEmail }),
            _ => Conflict(new { error = "conflict" }),
        };
    }

    /// <summary>TenantUsers linked to Entra object <paramref name="oid"/> in directory <paramref name="tid"/>.</summary>
    [HttpGet("memberships")]
    public async Task<ActionResult<IReadOnlyList<IdentityMembership>>> GetMemberships(
        [FromQuery] string tid, [FromQuery] string oid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tid) || string.IsNullOrWhiteSpace(oid))
            return BadRequest(new { error = "tid and oid are required" });
        return Ok(await _directory.GetMembershipsAsync(tid, oid, ct));
    }

    /// <summary>All tenants, or those registered to Entra directory <paramref name="azureTenantId"/>.</summary>
    [HttpGet("tenants")]
    public async Task<ActionResult<IReadOnlyList<IdentityTenant>>> GetTenants(
        [FromQuery] string? azureTenantId, CancellationToken ct)
        => Ok(await _directory.GetTenantsAsync(azureTenantId, ct));

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
