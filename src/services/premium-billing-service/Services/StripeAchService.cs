using PremiumBillingService.Models;
using Stripe;

namespace PremiumBillingService.Services;

/// <summary>
/// Integrates with Stripe ACH Direct Debit for programmatic premium drafts.
/// Uses Stripe PaymentIntents with us_bank_account payment method type.
/// </summary>
public interface IStripeAchService
{
    /// <summary>
    /// Create a Stripe ACH PaymentIntent to draft a sponsor's bank account
    /// </summary>
    Task<StripeAchDraftResult> CreateAchDraftAsync(
        string stripeCustomerId,
        string stripePaymentMethodId,
        decimal amount,
        string invoiceNumber,
        string groupNumber);

    /// <summary>
    /// Confirm a PaymentIntent (trigger the actual bank draft)
    /// </summary>
    Task<StripeAchDraftResult> ConfirmDraftAsync(string paymentIntentId);

    /// <summary>
    /// Cancel a pending PaymentIntent
    /// </summary>
    Task CancelDraftAsync(string paymentIntentId);

    /// <summary>
    /// Get the current status of a PaymentIntent
    /// </summary>
    Task<StripeAchDraftResult> GetDraftStatusAsync(string paymentIntentId);

    /// <summary>
    /// Process a Stripe webhook event for ACH payment updates
    /// </summary>
    Task<EftWebhookResult> ProcessWebhookAsync(string json, string stripeSignature);
}

public class StripeAchService : IStripeAchService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<StripeAchService> _logger;
    private readonly string _webhookSecret;

    public StripeAchService(IConfiguration configuration, ILogger<StripeAchService> logger)
    {
        _configuration = configuration;
        _logger = logger;

        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];
        _webhookSecret = configuration["Stripe:WebhookSecret"] ?? string.Empty;
    }

    public async Task<StripeAchDraftResult> CreateAchDraftAsync(
        string stripeCustomerId,
        string stripePaymentMethodId,
        decimal amount,
        string invoiceNumber,
        string groupNumber)
    {
        var service = new PaymentIntentService();

        var options = new PaymentIntentCreateOptions
        {
            Amount = (long)(amount * 100), // Stripe uses cents
            Currency = "usd",
            Customer = stripeCustomerId,
            PaymentMethod = stripePaymentMethodId,
            PaymentMethodTypes = new List<string> { "us_bank_account" },
            Confirm = true,
            Metadata = new Dictionary<string, string>
            {
                { "invoice_number", invoiceNumber },
                { "group_number", groupNumber },
                { "type", "premium_draft" }
            },
            Description = $"Premium billing draft for {invoiceNumber}",
            StatementDescriptor = "PREMIUM BILLING"
        };

        try
        {
            var paymentIntent = await service.CreateAsync(options);

            _logger.LogInformation(
                "Created Stripe ACH PaymentIntent {PaymentIntentId} for invoice {InvoiceNumber}, amount ${Amount:N2}",
                paymentIntent.Id, invoiceNumber, amount);

            return MapToResult(paymentIntent);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe ACH draft failed for invoice {InvoiceNumber}", invoiceNumber);
            return new StripeAchDraftResult
            {
                Status = "failed",
                ErrorMessage = ex.Message,
                ErrorCode = ex.StripeError?.Code
            };
        }
    }

    public async Task<StripeAchDraftResult> ConfirmDraftAsync(string paymentIntentId)
    {
        var service = new PaymentIntentService();

        try
        {
            var paymentIntent = await service.ConfirmAsync(paymentIntentId);
            _logger.LogInformation("Confirmed PaymentIntent {PaymentIntentId}", paymentIntentId);
            return MapToResult(paymentIntent);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to confirm PaymentIntent {PaymentIntentId}", paymentIntentId);
            return new StripeAchDraftResult
            {
                PaymentIntentId = paymentIntentId,
                Status = "failed",
                ErrorMessage = ex.Message,
                ErrorCode = ex.StripeError?.Code
            };
        }
    }

    public async Task CancelDraftAsync(string paymentIntentId)
    {
        var service = new PaymentIntentService();

        try
        {
            await service.CancelAsync(paymentIntentId);
            _logger.LogInformation("Cancelled PaymentIntent {PaymentIntentId}", paymentIntentId);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to cancel PaymentIntent {PaymentIntentId}", paymentIntentId);
            throw;
        }
    }

    public async Task<StripeAchDraftResult> GetDraftStatusAsync(string paymentIntentId)
    {
        var service = new PaymentIntentService();

        try
        {
            var paymentIntent = await service.GetAsync(paymentIntentId);
            return MapToResult(paymentIntent);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to get PaymentIntent {PaymentIntentId}", paymentIntentId);
            throw;
        }
    }

    public async Task<EftWebhookResult> ProcessWebhookAsync(string json, string stripeSignature)
    {
        try
        {
            var stripeEvent = EventUtility.ConstructEvent(json, stripeSignature, _webhookSecret);

            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    return await HandlePaymentSucceeded(stripeEvent);

                case "payment_intent.payment_failed":
                    return await HandlePaymentFailed(stripeEvent);

                case "payment_intent.canceled":
                    return HandlePaymentCancelled(stripeEvent);

                default:
                    _logger.LogInformation("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                    return new EftWebhookResult { Handled = false };
            }
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Error processing Stripe webhook");
            throw;
        }
    }

    private Task<EftWebhookResult> HandlePaymentSucceeded(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent == null)
            return Task.FromResult(new EftWebhookResult { Handled = false });

        var invoiceNumber = paymentIntent.Metadata.GetValueOrDefault("invoice_number");
        if (string.IsNullOrEmpty(invoiceNumber))
            return Task.FromResult(new EftWebhookResult { Handled = false });

        _logger.LogInformation(
            "ACH payment succeeded for invoice {InvoiceNumber}, PaymentIntent {PaymentIntentId}",
            invoiceNumber, paymentIntent.Id);

        return Task.FromResult(new EftWebhookResult
        {
            Handled = true,
            EventType = "payment_succeeded",
            PaymentIntentId = paymentIntent.Id,
            InvoiceNumber = invoiceNumber,
            Amount = paymentIntent.Amount / 100m,
            Status = "settled"
        });
    }

    private Task<EftWebhookResult> HandlePaymentFailed(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent == null)
            return Task.FromResult(new EftWebhookResult { Handled = false });

        var invoiceNumber = paymentIntent.Metadata.GetValueOrDefault("invoice_number");
        var failureMessage = paymentIntent.LastPaymentError?.Message ?? "Unknown failure";
        var failureCode = paymentIntent.LastPaymentError?.Code ?? "unknown";

        _logger.LogWarning(
            "ACH payment failed for invoice {InvoiceNumber}, PaymentIntent {PaymentIntentId}: {FailureCode} - {FailureMessage}",
            invoiceNumber, paymentIntent.Id, failureCode, failureMessage);

        return Task.FromResult(new EftWebhookResult
        {
            Handled = true,
            EventType = "payment_failed",
            PaymentIntentId = paymentIntent.Id,
            InvoiceNumber = invoiceNumber,
            Amount = paymentIntent.Amount / 100m,
            Status = "failed",
            FailureCode = failureCode,
            FailureMessage = failureMessage
        });
    }

    private EftWebhookResult HandlePaymentCancelled(Event stripeEvent)
    {
        var paymentIntent = stripeEvent.Data.Object as PaymentIntent;
        if (paymentIntent == null)
            return new EftWebhookResult { Handled = false };

        var invoiceNumber = paymentIntent.Metadata.GetValueOrDefault("invoice_number");

        _logger.LogInformation(
            "ACH payment cancelled for invoice {InvoiceNumber}, PaymentIntent {PaymentIntentId}",
            invoiceNumber, paymentIntent.Id);

        return new EftWebhookResult
        {
            Handled = true,
            EventType = "payment_cancelled",
            PaymentIntentId = paymentIntent.Id,
            InvoiceNumber = invoiceNumber,
            Status = "cancelled"
        };
    }

    private static StripeAchDraftResult MapToResult(PaymentIntent paymentIntent)
    {
        return new StripeAchDraftResult
        {
            PaymentIntentId = paymentIntent.Id,
            Status = paymentIntent.Status,
            Amount = paymentIntent.Amount / 100m,
            InvoiceNumber = paymentIntent.Metadata.GetValueOrDefault("invoice_number"),
            CreatedAt = paymentIntent.Created
        };
    }
}

/// <summary>
/// Result of a Stripe ACH draft operation
/// </summary>
public class StripeAchDraftResult
{
    public string? PaymentIntentId { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Result from processing a Stripe webhook event
/// </summary>
public class EftWebhookResult
{
    public bool Handled { get; set; }
    public string? EventType { get; set; }
    public string? PaymentIntentId { get; set; }
    public string? InvoiceNumber { get; set; }
    public decimal Amount { get; set; }
    public string? Status { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
}
