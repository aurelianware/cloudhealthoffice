using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stripe;
using TenantService.Security;
using TenantService.Services;

namespace TenantService.Controllers;

/// <summary>
/// Subscription billing. A tenant's own billing needs <c>settings:manage</c>
/// in that tenant (<see cref="RouteTenantFilter"/>); changing the tier is a
/// platform action. The Stripe webhook is anonymous and authenticated only by
/// Stripe's signature (<see cref="StripeWebhookVerifier"/>).
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class BillingController : ControllerBase
{
    private readonly IStripeService _stripeService;
    private readonly ITenantService _tenantService;
    private readonly StripeWebhookVerifier _webhookVerifier;
    private readonly TenantAuditLog _audit;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        IStripeService stripeService,
        ITenantService tenantService,
        StripeWebhookVerifier webhookVerifier,
        TenantAuditLog audit,
        ILogger<BillingController> logger)
    {
        _stripeService = stripeService;
        _tenantService = tenantService;
        _webhookVerifier = webhookVerifier;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>
    /// Create Stripe customer and subscription for tenant
    /// </summary>
    [HttpPost("tenants/{tenantId}/subscribe")]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateSubscription(string tenantId, [FromQuery] string tier = "starter")
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null)
        {
            return NotFound(new { error = $"Tenant {tenantId} not found" });
        }

        // Create Stripe customer if not exists
        if (string.IsNullOrEmpty(tenant.Billing?.StripeCustomerId))
        {
            var customerId = await _stripeService.CreateCustomerAsync(tenant);
            
            if (tenant.Billing == null)
                tenant.Billing = new Models.BillingInfo();
            
            tenant.Billing.StripeCustomerId = customerId;
            tenant.Billing.BillingEmail = tenant.ContactInfo.Email;
            
            await _tenantService.UpdateTenantAsync(tenantId, new Models.UpdateTenantRequest());
        }

        // Create subscription
        var subscriptionId = await _stripeService.CreateSubscriptionAsync(tenant.Billing!.StripeCustomerId!, tier);

        return Ok(new
        {
            customerId = tenant.Billing.StripeCustomerId,
            subscriptionId,
            tier
        });
    }

    /// <summary>
    /// Get upcoming invoice for tenant
    /// </summary>
    [HttpGet("tenants/{tenantId}/upcoming-invoice")]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetUpcomingInvoice(string tenantId)
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null || tenant.Billing?.StripeCustomerId == null)
        {
            return NotFound(new { error = "Tenant not found or no billing setup" });
        }

        var invoice = await _stripeService.GetUpcomingInvoiceAsync(tenant.Billing.StripeCustomerId);
        if (invoice == null)
        {
            return Ok(new { message = "No upcoming invoice" });
        }

        return Ok(new
        {
            amount = invoice.AmountDue / 100.0, // Convert cents to dollars
            currency = invoice.Currency,
            dueDate = invoice.DueDate,
            periodStart = invoice.PeriodStart,
            periodEnd = invoice.PeriodEnd
        });
    }

    /// <summary>
    /// Get invoice history for tenant
    /// </summary>
    [HttpGet("tenants/{tenantId}/invoices")]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoices(string tenantId, [FromQuery] int limit = 12)
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null || tenant.Billing?.StripeCustomerId == null)
        {
            return NotFound(new { error = "Tenant not found or no billing setup" });
        }

        var invoices = await _stripeService.GetInvoicesAsync(tenant.Billing.StripeCustomerId, limit);

        var result = invoices.Select(inv => new
        {
            id = inv.Id,
            amount = inv.AmountDue / 100.0,
            currency = inv.Currency,
            status = inv.Status,
            created = inv.Created,
            dueDate = inv.DueDate,
            pdfUrl = inv.InvoicePdf
        });

        return Ok(result);
    }

    /// <summary>
    /// Stripe webhook endpoint (payment events, subscription changes, etc.).
    /// Anonymous: Stripe cannot present a CHO token. The request is acted on
    /// only when its <c>Stripe-Signature</c> verifies against the configured
    /// endpoint secret; without a configured secret every request is refused
    /// (503), so a missing secret can never open the endpoint.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> HandleStripeWebhook()
    {
        if (!_webhookVerifier.IsConfigured)
        {
            _logger.LogError("Stripe webhook refused: Stripe:WebhookSecret is not configured");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "webhook_not_configured" });
        }

        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeSignature = Request.Headers["Stripe-Signature"].ToString();

        if (string.IsNullOrEmpty(stripeSignature))
        {
            return BadRequest(new { error = "Missing Stripe-Signature header" });
        }

        Event stripeEvent;
        try
        {
            stripeEvent = _webhookVerifier.Verify(json, stripeSignature);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning("Stripe webhook refused: signature did not verify ({Reason})", ex.GetType().Name);
            return BadRequest(new { error = "invalid_signature" });
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Signed but unreadable (for example an unexpected event shape).
            _logger.LogWarning("Stripe webhook refused: event could not be parsed ({Reason})", ex.GetType().Name);
            return BadRequest(new { error = "invalid_event" });
        }

        try
        {
            await _stripeService.HandleEventAsync(stripeEvent);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook event {EventId}", stripeEvent.Id);
            return BadRequest(new { error = "webhook_processing_failed" });
        }
    }

    /// <summary>
    /// Cancel subscription for tenant
    /// </summary>
    [HttpPost("tenants/{tenantId}/cancel")]
    [RequirePermission(TenantPermissions.SettingsManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelSubscription(string tenantId)
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null || tenant.Billing?.StripeSubscriptionId == null)
        {
            return NotFound(new { error = "Tenant not found or no active subscription" });
        }

        await _stripeService.CancelSubscriptionAsync(tenant.Billing.StripeSubscriptionId);

        // Suspend tenant
        await _tenantService.SuspendTenantAsync(tenantId);

        return Ok(new { message = "Subscription canceled, tenant suspended" });
    }

    /// <summary>
    /// Update subscription tier (upgrade/downgrade)
    /// </summary>
    [HttpPut("tenants/{tenantId}/tier")]
    [RequirePermission(TenantPermissions.PlatformTenants)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSubscriptionTier(string tenantId, [FromQuery] string newTier)
    {
        var tenant = await _tenantService.GetTenantAsync(tenantId);
        if (tenant == null || tenant.Billing?.StripeSubscriptionId == null)
        {
            return NotFound(new { error = "Tenant not found or no active subscription" });
        }

        await _stripeService.UpdateSubscriptionAsync(tenant.Billing.StripeSubscriptionId, newTier);

        // Update tenant record
        await _tenantService.UpdateTenantAsync(tenantId, new Models.UpdateTenantRequest
        {
            SubscriptionTier = newTier
        });

        _audit.Record("change subscription tier to " + newTier, tenantId);
        return Ok(new { message = $"Subscription updated to {newTier}" });
    }
}
