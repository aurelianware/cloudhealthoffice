using Stripe;
using TenantService.Models;
using CloudHealthOffice.Infrastructure.Logging;

namespace TenantService.Services;

/// <summary>
/// Stripe billing integration for subscription management
/// </summary>
public interface IStripeService
{
    Task<string> CreateCustomerAsync(Tenant tenant);
    Task<string> CreateSubscriptionAsync(string customerId, string subscriptionTier);
    Task CancelSubscriptionAsync(string subscriptionId);
    Task UpdateSubscriptionAsync(string subscriptionId, string newTier);
    Task<Stripe.Invoice?> GetUpcomingInvoiceAsync(string customerId);
    Task<IEnumerable<Stripe.Invoice>> GetInvoicesAsync(string customerId, int limit = 12);

    /// <summary>
    /// Applies a webhook event. The caller must have verified its signature
    /// with <see cref="StripeWebhookVerifier"/> first.
    /// </summary>
    Task HandleEventAsync(Event stripeEvent);
}

public class StripeService : IStripeService
{
    private readonly IConfiguration _configuration;
    private readonly ITenantRepository _tenantRepository;
    private readonly ILogger<StripeService> _logger;

    public StripeService(
        IConfiguration configuration,
        ITenantRepository tenantRepository,
        ILogger<StripeService> logger)
    {
        _configuration = configuration;
        _tenantRepository = tenantRepository;
        _logger = logger;

        // Configure Stripe API key
        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];
    }

    public async Task<string> CreateCustomerAsync(Tenant tenant)
    {
        var options = new CustomerCreateOptions
        {
            Email = tenant.ContactInfo.Email,
            Name = tenant.OrganizationName,
            Description = $"Tenant: {tenant.TenantId}",
            Metadata = new Dictionary<string, string>
            {
                { "tenant_id", tenant.TenantId },
                { "tenant_name", tenant.TenantName },
                { "subscription_tier", tenant.SubscriptionTier }
            }
        };

        var service = new CustomerService();
        var customer = await service.CreateAsync(options);

        _logger.LogInformation("Created Stripe customer {CustomerId} for tenant {TenantId}", customer.Id, tenant.TenantId);

        return customer.Id;
    }

    public async Task<string> CreateSubscriptionAsync(string customerId, string subscriptionTier)
    {
        var priceId = GetPriceId(subscriptionTier);

        var options = new SubscriptionCreateOptions
        {
            Customer = customerId,
            Items = new List<SubscriptionItemOptions>
            {
                new SubscriptionItemOptions
                {
                    Price = priceId,
                }
            },
            PaymentBehavior = "default_incomplete",
            PaymentSettings = new SubscriptionPaymentSettingsOptions
            {
                SaveDefaultPaymentMethod = "on_subscription"
            },
            Expand = new List<string> { "latest_invoice.payment_intent" }
        };

        var service = new SubscriptionService();
        var subscription = await service.CreateAsync(options);

        _logger.LogInformation("Created Stripe subscription {SubscriptionId} for customer {CustomerId}", subscription.Id, customerId);

        return subscription.Id;
    }

    public async Task CancelSubscriptionAsync(string subscriptionId)
    {
        var service = new SubscriptionService();
        await service.CancelAsync(subscriptionId, new SubscriptionCancelOptions());

        _logger.LogInformation("Canceled Stripe subscription {SubscriptionId}", subscriptionId);
    }

    public async Task UpdateSubscriptionAsync(string subscriptionId, string newTier)
    {
        var service = new SubscriptionService();
        var subscription = await service.GetAsync(subscriptionId);

        var newPriceId = GetPriceId(newTier);

        var options = new SubscriptionUpdateOptions
        {
            Items = new List<SubscriptionItemOptions>
            {
                new SubscriptionItemOptions
                {
                    Id = subscription.Items.Data[0].Id,
                    Price = newPriceId
                }
            },
            ProrationBehavior = "create_prorations"
        };

        await service.UpdateAsync(subscriptionId, options);

        _logger.LogInformation("Updated Stripe subscription {SubscriptionId} to tier {Tier}", LogSanitizer.Sanitize(subscriptionId), LogSanitizer.Sanitize(newTier));
    }

    public async Task<Stripe.Invoice?> GetUpcomingInvoiceAsync(string customerId)
    {
        try
        {
            var service = new InvoiceService();
            var invoice = await service.UpcomingAsync(new UpcomingInvoiceOptions
            {
                Customer = customerId
            });

            return invoice;
        }
        catch (StripeException)
        {
            return null;
        }
    }

    public async Task<IEnumerable<Stripe.Invoice>> GetInvoicesAsync(string customerId, int limit = 12)
    {
        var service = new InvoiceService();
        var invoices = await service.ListAsync(new InvoiceListOptions
        {
            Customer = customerId,
            Limit = limit
        });

        return invoices.Data;
    }

    public async Task HandleEventAsync(Event stripeEvent)
    {
        try
        {
            _logger.LogInformation("Processing Stripe webhook event: {EventType}", stripeEvent.Type);

            switch (stripeEvent.Type)
            {
                
                case EventTypes.CustomerSubscriptionCreated:

                    await HandleSubscriptionCreatedAsync(stripeEvent);
                    break;

                case EventTypes.CustomerSubscriptionUpdated:
                    await HandleSubscriptionUpdatedAsync(stripeEvent);
                    break;

                case EventTypes.CustomerSubscriptionDeleted:
                    await HandleSubscriptionDeletedAsync(stripeEvent);
                    break;

                case EventTypes.InvoicePaymentSucceeded:
                    await HandlePaymentSucceededAsync(stripeEvent);
                    break;

                case EventTypes.InvoicePaymentFailed:
                    await HandlePaymentFailedAsync(stripeEvent);
                    break;

                default:
                    _logger.LogInformation("Unhandled event type: {EventType}", stripeEvent.Type);
                    break;
            }
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook");
            throw;
        }
    }

    /// <summary>
    /// A webhook's change is re-read and re-applied this many times when an
    /// admin edit (or another webhook) saved the tenant between its read and its
    /// write, so the event is not lost to a conflict. After that the conflict
    /// propagates, the webhook answers non-2xx and Stripe retries it.
    /// </summary>
    public const int MaxWebhookWriteAttempts = 5;

    /// <summary>
    /// Reads the tenant, applies <paramref name="apply"/> and saves it,
    /// conditional on the revision read; on a conflict, re-reads and re-applies.
    /// <paramref name="apply"/> returns false when there is nothing to write.
    /// Null when the tenant does not exist.
    /// </summary>
    private async Task<Tenant?> ApplyToTenantAsync(string tenantId, Func<Tenant, bool> apply)
    {
        for (var attempt = 1; ; attempt++)
        {
            var tenant = await _tenantRepository.GetByTenantIdAsync(tenantId);
            if (tenant == null) return null;
            if (!apply(tenant)) return tenant;
            try
            {
                return await _tenantRepository.UpdateAsync(tenant);
            }
            catch (TenantChangedConcurrentlyException) when (attempt < MaxWebhookWriteAttempts)
            {
                _logger.LogInformation(
                    "Tenant {TenantId} changed while a Stripe webhook was applied (attempt {Attempt}); re-reading",
                    LogSanitizer.Sanitize(tenantId), attempt);
            }
        }
    }

    private async Task HandleSubscriptionCreatedAsync(Event stripeEvent)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        if (subscription == null) return;

        var tenantId = subscription.Metadata.GetValueOrDefault("tenant_id");
        if (string.IsNullOrEmpty(tenantId)) return;

        var tenant = await ApplyToTenantAsync(tenantId, t =>
        {
            t.Billing = new BillingInfo
            {
                StripeCustomerId = subscription.CustomerId,
                StripeSubscriptionId = subscription.Id,
                BillingEmail = t.ContactInfo.Email,
                CurrentPeriodStart = subscription.CurrentPeriodStart,
                CurrentPeriodEnd = subscription.CurrentPeriodEnd,
                NextBillingDate = subscription.CurrentPeriodEnd
            };
            return true;
        });
        if (tenant == null) return;

        _logger.LogInformation("Subscription created for tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
    }

    private async Task HandleSubscriptionUpdatedAsync(Event stripeEvent)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        if (subscription == null) return;

        var tenantId = subscription.Metadata.GetValueOrDefault("tenant_id");
        if (string.IsNullOrEmpty(tenantId)) return;

        var tenant = await ApplyToTenantAsync(tenantId, t =>
        {
            if (t.Billing != null)
            {
                t.Billing.CurrentPeriodStart = subscription.CurrentPeriodStart;
                t.Billing.CurrentPeriodEnd = subscription.CurrentPeriodEnd;
                t.Billing.NextBillingDate = subscription.CurrentPeriodEnd;
            }
            return true;
        });
        if (tenant == null) return;

        _logger.LogInformation("Subscription updated for tenant {TenantId}", LogSanitizer.Sanitize(tenantId));
    }

    private async Task HandleSubscriptionDeletedAsync(Event stripeEvent)
    {
        var subscription = stripeEvent.Data.Object as Subscription;
        if (subscription == null) return;

        var tenantId = subscription.Metadata.GetValueOrDefault("tenant_id");
        if (string.IsNullOrEmpty(tenantId)) return;

        // Suspend tenant on subscription cancellation
        var tenant = await ApplyToTenantAsync(tenantId, t =>
        {
            t.Status = "suspended";
            return true;
        });
        if (tenant == null) return;

        _logger.LogWarning("Subscription canceled for tenant {TenantId}, tenant suspended", LogSanitizer.Sanitize(tenantId));
    }

    private async Task HandlePaymentSucceededAsync(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as Stripe.Invoice;
        if (invoice == null) return;

        var customerId = invoice.CustomerId;
        var tenant = await FindTenantByStripeCustomerIdAsync(customerId);
        if (tenant == null) return;

        // Ensure tenant is active on successful payment
        if (tenant.Status == "suspended")
        {
            await ApplyToTenantAsync(tenant.TenantId, t =>
            {
                if (t.Status != "suspended") return false;
                t.Status = "active";
                return true;
            });
        }

        _logger.LogInformation("Payment succeeded for tenant {TenantId}, amount: {Amount}", tenant.TenantId, invoice.AmountPaid);
    }

    private async Task HandlePaymentFailedAsync(Event stripeEvent)
    {
        var invoice = stripeEvent.Data.Object as Stripe.Invoice;
        if (invoice == null) return;

        var customerId = invoice.CustomerId;
        var tenant = await FindTenantByStripeCustomerIdAsync(customerId);
        if (tenant == null) return;

        // TODO: Implement dunning process (email notifications, grace period, etc.)
        _logger.LogWarning("Payment failed for tenant {TenantId}, invoice: {InvoiceId}", tenant.TenantId, invoice.Id);
    }

    private async Task<Tenant?> FindTenantByStripeCustomerIdAsync(string customerId)
    {
        var tenants = await _tenantRepository.GetAllAsync();
        return tenants.FirstOrDefault(t => t.Billing?.StripeCustomerId == customerId);
    }

    private string GetPriceId(string tier)
    {
        var priceIds = _configuration.GetSection("Stripe:PricingIds");
        return tier.ToLower() switch
        {
            "starter" => priceIds["starter_monthly"] ?? throw new InvalidOperationException("Starter pricing ID not configured"),
            "professional" => priceIds["professional_monthly"] ?? throw new InvalidOperationException("Professional pricing ID not configured"),
            "enterprise" => priceIds["enterprise_monthly"] ?? throw new InvalidOperationException("Enterprise pricing ID not configured"),
            _ => throw new ArgumentException($"Unknown subscription tier: {tier}")
        };
    }
}
