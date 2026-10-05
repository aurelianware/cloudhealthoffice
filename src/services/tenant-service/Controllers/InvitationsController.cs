using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using TenantService.Models;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Invitations for people to join the tenant, typically from another Entra
/// directory. Every action needs <c>users:manage</c> in the route's tenant
/// (<see cref="RouteTenantFilter"/>). The code and link are in the response to
/// create and resend only; they cannot be read back. Redemption is
/// token-service's (<see cref="InternalIdentityController"/>).
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId}/invitations")]
[RequirePermission(TenantPermissions.UsersManage)]
public class InvitationsController : ControllerBase
{
    private readonly InvitationService _invitations;

    public InvitationsController(InvitationService invitations) => _invitations = invitations;

    [HttpPost]
    [ProducesResponseType(typeof(IssuedInvitationResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(string tenantId, [FromBody] CreateInvitationRequest request, CancellationToken ct)
        => await Run(async () =>
        {
            var issued = await _invitations.CreateAsync(tenantId, request, ct);
            NoStore();
            return StatusCode(StatusCodes.Status201Created, issued);
        });

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<InvitationView>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string tenantId, CancellationToken ct)
        => Ok(await _invitations.ListAsync(tenantId, ct));

    [HttpPost("{invitationId}/revoke")]
    [ProducesResponseType(typeof(InvitationView), StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(string tenantId, string invitationId, CancellationToken ct)
        => await Run(async () => Ok(await _invitations.RevokeAsync(tenantId, invitationId, ct)));

    /// <summary>Issues a new code (the old one stops working) and resets the expiry.</summary>
    [HttpPost("{invitationId}/resend")]
    [ProducesResponseType(typeof(IssuedInvitationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resend(string tenantId, string invitationId, CancellationToken ct)
        => await Run(async () =>
        {
            var issued = await _invitations.ResendAsync(tenantId, invitationId, ct);
            NoStore();
            return Ok(issued);
        });

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvitationRequestException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Error, message = ex.Message });
        }
    }
}
