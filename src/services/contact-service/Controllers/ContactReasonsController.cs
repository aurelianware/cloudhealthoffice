using CloudHealthOffice.Infrastructure.Security;
using ContactService.Models;
using ContactService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace ContactService.Controllers;

/// <summary>
/// The tenant's contact reason taxonomy: the list a rep picks from and every
/// service report is counted by.
///
/// Reading needs <c>service:read</c>, the default. Changing the taxonomy
/// needs <c>service:admin</c>, because renaming or retiring a reason changes
/// what last quarter's numbers mean.
///
/// See docs/architecture/cho-service.md §3.
/// </summary>
[ApiController]
[Route("api/contact-reasons")]
[Produces("application/json")]
public sealed class ContactReasonsController(
    IContactReasonRepository reasons,
    ILogger<ContactReasonsController> logger) : ControllerBase
{
    /// <summary>
    /// The taxonomy. <c>activeOnly</c> is what the contact screen asks for;
    /// the full list is what the configuration screen shows, so a supervisor
    /// can see what has been retired.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ContactReason>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ContactReason>>> List(
        [FromQuery] bool activeOnly = false, CancellationToken ct = default) =>
        Ok(await reasons.ListAsync(activeOnly, ct));

    /// <summary>
    /// Creates a reason, or updates the one with the same code. The code is
    /// the identity: contacts carry it, so changing a label is safe and
    /// changing a code is a new reason.
    /// </summary>
    [HttpPut]
    [RequirePermission(ChoRolePermissions.ServiceAdmin)]
    [ProducesResponseType(typeof(ContactReason), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ContactReason>> Save(
        [FromBody] SaveContactReasonRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code)) return BadRequest("code is required");
        if (string.IsNullOrWhiteSpace(request.Label)) return BadRequest("label is required");

        return Ok(await reasons.SaveAsync(new ContactReason
        {
            Code = request.Code,
            Label = request.Label.Trim(),
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            Order = request.Order,
            IsActive = request.IsActive,
            IsComplaintIndicator = request.IsComplaintIndicator,
        }, ct));
    }

    /// <summary>
    /// Writes the starter taxonomy for a tenant that has none, so the contact
    /// screen works on the first day. A tenant with any reason at all is left
    /// alone and the call reports zero.
    /// </summary>
    [HttpPost("seed")]
    [RequirePermission(ChoRolePermissions.ServiceAdmin)]
    [ProducesResponseType(typeof(SeedResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<SeedResult>> Seed(CancellationToken ct)
    {
        var added = await reasons.SeedStarterReasonsAsync(ct);
        if (added > 0) logger.LogInformation("Seeded {Count} starter contact reasons.", added);
        return Ok(new SeedResult(added));
    }

    /// <param name="Added">How many reasons were written; zero when the tenant already had some.</param>
    public sealed record SeedResult(int Added);
}
