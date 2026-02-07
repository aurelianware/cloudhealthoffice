using Microsoft.AspNetCore.Mvc;
using TenantService.Services;

namespace TenantService.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class BillingController : ControllerBase
{
    private readonly IStripeService _stripeService;
    private readonly ITenantService _tenantService;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        IStripeService stripeService,
        ITenantService tenantService,
        ILogger<BillingController> logger)
    {
        _stripeService = stripeService;
        _tenantService = tenantService;
        _logger = logger;
    }

    /// <summary>
    /// Create Stripe customer and subscription for tenant
    /// </summary>
    [HttpPost("tenants/{tenantId}/subscribe")]
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
    /// Stripe webhook endpoint (payment events, subscription changes, etc.)
    /// </summary>
    [HttpPost("webhook")]
    public async Task<IActionResult> HandleStripeWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeSignature = Request.Headers["Stripe-Signature"];

        if (string.IsNullOrEmpty(stripeSignature))
        {
            return BadRequest(new { error = "Missing Stripe-Signature header" });
        }

        try
        {
            await _stripeService.HandleWebhookAsync(json, stripeSignature!);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel subscription for tenant
    /// </summary>
    [HttpPost("tenants/{tenantId}/cancel")]
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

        return Ok(new { message = $"Subscription updated to {newTier}" });
    }
}
