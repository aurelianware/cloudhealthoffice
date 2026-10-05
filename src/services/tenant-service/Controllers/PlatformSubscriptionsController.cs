using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Subscription records (the portal's PlatformTenants page): list, create,
/// edit, change status and delete, addressed by Entra directory
/// (<c>azureTenantId</c>). Cross-tenant platform administration: every action
/// needs <c>platform:tenants</c> (tenant roles, even through *:*, and service
/// tokens never hold it), and every write is audited. The portal used to write
/// these records to the database itself.
/// </summary>
[ApiController]
[Route("api/v1/platform/subscriptions")]
[RequirePermission(TenantPermissions.PlatformTenants)]
public class PlatformSubscriptionsController : ControllerBase
{
    private readonly ISubscriptionStore _store;
    private readonly TenantAuditLog _audit;

    public PlatformSubscriptionsController(ISubscriptionStore store, TenantAuditLog audit)
    {
        _store = store;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SubscriptionRecord>>> List(CancellationToken ct)
    {
        var records = await _store.ListAsync(ct);
        _audit.Record("list subscriptions", "*");
        return Ok(records);
    }

    [HttpPost]
    public async Task<ActionResult<SubscriptionRecord>> Create([FromBody] SubscriptionWrite request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AzureTenantId) || string.IsNullOrWhiteSpace(request.OrganizationName))
            return BadRequest(new { error = "azureTenantId and organizationName are required" });
        if (Invalid(request) is { } invalid)
            return invalid;

        var created = await _store.CreateAsync(request, ct);
        _audit.Record($"create subscription (directory {request.AzureTenantId}, status {created.SubscriptionStatus}, tier {created.Tier})", created.TenantId);
        return Created($"/api/v1/platform/subscriptions/{Uri.EscapeDataString(created.AzureTenantId)}", created);
    }

    [HttpPut("{azureTenantId}")]
    public async Task<IActionResult> Update(string azureTenantId, [FromBody] SubscriptionWrite request, CancellationToken ct)
    {
        if (Invalid(request) is { } invalid)
            return invalid;
        if (!await _store.UpdateAsync(azureTenantId, request, ct))
            return NotFound(new { error = "No subscription for that directory" });
        _audit.Record($"update subscription (directory {azureTenantId}, status {request.SubscriptionStatus}, tier {request.Tier})", azureTenantId);
        return NoContent();
    }

    [HttpPut("{azureTenantId}/status")]
    public async Task<IActionResult> SetStatus(string azureTenantId, [FromBody] SubscriptionStatusRequest request, CancellationToken ct)
    {
        if (request?.Status is null || !SubscriptionRules.Statuses.Contains(request.Status))
            return BadRequest(new { error = $"status must be one of {string.Join(", ", SubscriptionRules.Statuses)}" });
        if (!await _store.SetStatusAsync(azureTenantId, request.Status, ct))
            return NotFound(new { error = "No subscription for that directory" });
        _audit.Record($"set subscription status {request.Status} (directory {azureTenantId})", azureTenantId);
        return NoContent();
    }

    [HttpDelete("{azureTenantId}")]
    public async Task<IActionResult> Delete(string azureTenantId, CancellationToken ct)
    {
        if (!await _store.DeleteAsync(azureTenantId, ct))
            return NotFound(new { error = "No subscription for that directory" });
        _audit.Record($"delete subscription (directory {azureTenantId})", azureTenantId);
        return NoContent();
    }

    private ObjectResult? Invalid(SubscriptionWrite request)
    {
        if (request.SubscriptionStatus != null && !SubscriptionRules.Statuses.Contains(request.SubscriptionStatus))
            return BadRequest(new { error = $"subscriptionStatus must be one of {string.Join(", ", SubscriptionRules.Statuses)}" });
        if (request.Tier != null && !SubscriptionRules.Tiers.Contains(request.Tier))
            return BadRequest(new { error = $"tier must be one of {string.Join(", ", SubscriptionRules.Tiers)}" });
        if (!SubscriptionRules.IsStripeCustomerId(request.StripeCustomerId) || !SubscriptionRules.IsStripeSubscriptionId(request.StripeSubscriptionId))
            return BadRequest(new { error = "Stripe ids are malformed" });
        return null;
    }
}

public sealed class SubscriptionStatusRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string? Status { get; set; }
}
