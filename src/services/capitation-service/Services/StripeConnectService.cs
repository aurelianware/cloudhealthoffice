using CapitationService.Models;
using Stripe;

namespace CapitationService.Services;

/// <summary>
/// Thin wrapper around Stripe SDK types so that StripeConnectService can be unit-tested
/// without hitting the live Stripe API. In production the default implementation delegates
/// straight to the SDK; in tests the mock replaces it.
/// </summary>
public interface IStripeTransferClient
{
    Task<Transfer> CreateTransferAsync(TransferCreateOptions options, RequestOptions requestOptions);
    Task<Transfer> GetTransferAsync(string transferId);
    Task CreateTransferReversalAsync(string transferId);
    Event ConstructWebhookEvent(string json, string signature, string secret);
}

public class StripeTransferClient : IStripeTransferClient
{
    public async Task<Transfer> CreateTransferAsync(TransferCreateOptions options, RequestOptions requestOptions)
        => await new TransferService().CreateAsync(options, requestOptions);

    public async Task<Transfer> GetTransferAsync(string transferId)
        => await new TransferService().GetAsync(transferId);

    public async Task CreateTransferReversalAsync(string transferId)
        => await new TransferReversalService().CreateAsync(transferId, new TransferReversalCreateOptions());

    public Event ConstructWebhookEvent(string json, string signature, string secret)
        => EventUtility.ConstructEvent(json, signature, secret);
}

/// <summary>
/// Integrates with Stripe Connect for capitation disbursements to providers.
/// Uses Stripe Transfers to Connected Accounts (not PaymentIntents).
/// This is the credit-side equivalent of StripeAchService which uses PaymentIntents
/// for debiting sponsor bank accounts.
/// </summary>
public interface IStripeConnectService
{
    /// <summary>
    /// Create a Stripe Transfer to a provider's Connected Account, with an
    /// idempotency key derived from <paramref name="disbursementId"/>: a retry
    /// of the same disbursement never creates a second transfer. Returns
    /// Status "failed" only when Stripe definitively refused it (no transfer
    /// was made); any other error is thrown, and the outcome is then unknown.
    /// </summary>
    Task<StripeTransferResult> CreateTransferAsync(
        string stripeConnectedAccountId,
        decimal amount,
        string statementNumber,
        string providerNpi,
        string tenantId,
        string disbursementId);

    /// <summary>
    /// Get the current status of a Transfer
    /// </summary>
    Task<StripeTransferResult> GetTransferStatusAsync(string transferId);

    /// <summary>
    /// Cancel/reverse a pending Transfer
    /// </summary>
    Task CancelTransferAsync(string transferId);

    /// <summary>
    /// Process a Stripe webhook event for transfer/payout updates
    /// </summary>
    Task<DisbursementWebhookResult> ProcessWebhookAsync(string json, string stripeSignature);
}

public class StripeConnectService : IStripeConnectService
{
    /// <summary>
    /// Transfer metadata key carrying the CHO tenant. The Stripe webhook is
    /// anonymous; this signed value is how an event finds its tenant.
    /// </summary>
    public const string TenantMetadataKey = "tenant_id";

    /// <summary>Transfer metadata key carrying the CHO disbursement id.</summary>
    public const string DisbursementMetadataKey = "disbursement_id";

    /// <summary>
    /// The Stripe idempotency key for a disbursement's transfer: the same for
    /// every attempt of that disbursement, so Stripe returns the first
    /// transfer instead of creating another.
    /// </summary>
    public static string TransferIdempotencyKey(string disbursementId) => $"cho-capitation-transfer-{disbursementId}";

    /// <summary>
    /// True when Stripe answered and refused the request (a card or
    /// invalid-request error, 4xx other than 409): no transfer was made.
    /// A 5xx, a 409 (idempotency or lock conflict), or an error without
    /// Stripe's answer leaves the outcome unknown.
    /// </summary>
    public static bool IsDefinitiveRefusal(StripeException ex)
    {
        var status = (int)ex.HttpStatusCode;
        var type = ex.StripeError?.Type;
        return status is >= 400 and < 500 && status != 409
            && (type == "card_error" || type == "invalid_request_error");
    }

    private readonly IStripeTransferClient _stripeClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StripeConnectService> _logger;
    private readonly string _webhookSecret;

    public StripeConnectService(
        IStripeTransferClient stripeClient,
        IConfiguration configuration,
        ILogger<StripeConnectService> logger)
    {
        _stripeClient = stripeClient;
        _configuration = configuration;
        _logger = logger;

        StripeConfiguration.ApiKey = configuration["Stripe:SecretKey"];
        // The first secret actually set: appsettings.json carries both keys as "",
        // and Kubernetes sets only Stripe__WebhookSecret.
        _webhookSecret = new[] { configuration["Stripe:ConnectWebhookSecret"], configuration["Stripe:WebhookSecret"] }
            .FirstOrDefault(StripeWebhookSecret.IsUsable) ?? string.Empty;
    }

    public async Task<StripeTransferResult> CreateTransferAsync(
        string stripeConnectedAccountId,
        decimal amount,
        string statementNumber,
        string providerNpi,
        string tenantId,
        string disbursementId)
    {
        var options = new TransferCreateOptions
        {
            Amount = (long)(amount * 100), // Stripe uses cents
            Currency = "usd",
            Destination = stripeConnectedAccountId,
            Metadata = new Dictionary<string, string>
            {
                { "statement_number", statementNumber },
                { "provider_npi", providerNpi },
                { "type", "capitation" },
                { TenantMetadataKey, tenantId },
                { DisbursementMetadataKey, disbursementId }
            },
            Description = $"Capitation payment for {statementNumber}"
        };
        var requestOptions = new RequestOptions { IdempotencyKey = TransferIdempotencyKey(disbursementId) };

        try
        {
            var transfer = await _stripeClient.CreateTransferAsync(options, requestOptions);

            _logger.LogInformation(
                "Created Stripe Transfer {TransferId} for statement {StatementNumber}, amount ${Amount:N2}",
                transfer.Id, statementNumber, amount);

            return MapToResult(transfer);
        }
        catch (StripeException ex) when (IsDefinitiveRefusal(ex))
        {
            // Stripe said no: no transfer exists. Anything else propagates, since
            // the transfer may exist (the caller must not make the statement payable again).
            _logger.LogError(ex, "Stripe Transfer refused for statement {StatementNumber}",
                statementNumber);
            return new StripeTransferResult
            {
                Status = "failed",
                ErrorMessage = ex.Message,
                ErrorCode = ex.StripeError?.Code
            };
        }
    }

    public async Task<StripeTransferResult> GetTransferStatusAsync(string transferId)
    {
        try
        {
            var transfer = await _stripeClient.GetTransferAsync(transferId);
            return MapToResult(transfer);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to get Transfer {TransferId}", transferId);
            throw;
        }
    }

    public async Task CancelTransferAsync(string transferId)
    {
        try
        {
            await _stripeClient.CreateTransferReversalAsync(transferId);
            _logger.LogInformation("Reversed Transfer {TransferId}", transferId);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Failed to reverse Transfer {TransferId}", transferId);
            throw;
        }
    }

    public async Task<DisbursementWebhookResult> ProcessWebhookAsync(string json, string stripeSignature)
    {
        // The webhook endpoint is reachable without a CHO token; the signature is
        // its only authentication. An empty secret would make any body "valid".
        if (!StripeWebhookSecret.IsUsable(_webhookSecret))
            throw new InvalidOperationException("Stripe webhook secret is not configured; webhook rejected");

        try
        {
            var stripeEvent = _stripeClient.ConstructWebhookEvent(json, stripeSignature, _webhookSecret);

            switch (stripeEvent.Type)
            {
                case "transfer.created":
                    return HandleTransferCreated(stripeEvent);

                case "transfer.reversed":
                    return HandleTransferReversed(stripeEvent);

                case "payout.paid":
                    return await HandlePayoutPaid(stripeEvent);

                case "payout.failed":
                    return await HandlePayoutFailed(stripeEvent);

                default:
                    _logger.LogInformation("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                    return new DisbursementWebhookResult { Handled = false };
            }
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Error processing Stripe Connect webhook");
            throw;
        }
    }

    private DisbursementWebhookResult HandleTransferCreated(Event stripeEvent)
    {
        var transfer = stripeEvent.Data.Object as Transfer;
        if (transfer == null)
            return new DisbursementWebhookResult { Handled = false };

        var statementNumber = transfer.Metadata.GetValueOrDefault("statement_number");

        _logger.LogInformation(
            "Transfer created {TransferId} for statement {StatementNumber}",
            transfer.Id, statementNumber);

        return new DisbursementWebhookResult
        {
            Handled = true,
            EventType = "transfer_created",
            TransferId = transfer.Id,
            StatementNumber = statementNumber,
            TenantId = transfer.Metadata?.GetValueOrDefault(TenantMetadataKey),
            Amount = transfer.Amount / 100m,
            Status = "submitted"
        };
    }

    private DisbursementWebhookResult HandleTransferReversed(Event stripeEvent)
    {
        var transfer = stripeEvent.Data.Object as Transfer;
        if (transfer == null)
            return new DisbursementWebhookResult { Handled = false };

        var statementNumber = transfer.Metadata.GetValueOrDefault("statement_number");

        _logger.LogWarning(
            "Transfer reversed {TransferId} for statement {StatementNumber}",
            transfer.Id, statementNumber);

        return new DisbursementWebhookResult
        {
            Handled = true,
            EventType = "transfer_reversed",
            TransferId = transfer.Id,
            StatementNumber = statementNumber,
            TenantId = transfer.Metadata?.GetValueOrDefault(TenantMetadataKey),
            Amount = transfer.Amount / 100m,
            Status = "returned",
            FailureCode = "TRANSFER_REVERSED",
            FailureMessage = "Transfer was reversed"
        };
    }

    private Task<DisbursementWebhookResult> HandlePayoutPaid(Event stripeEvent)
    {
        var payout = stripeEvent.Data.Object as Payout;
        if (payout == null)
            return Task.FromResult(new DisbursementWebhookResult { Handled = false });

        _logger.LogInformation("Payout paid {PayoutId}, amount ${Amount:N2}",
            payout.Id, payout.Amount / 100m);

        return Task.FromResult(new DisbursementWebhookResult
        {
            Handled = true,
            EventType = "payout_paid",
            TransferId = payout.Id,
            TenantId = payout.Metadata?.GetValueOrDefault(TenantMetadataKey),
            Amount = payout.Amount / 100m,
            Status = "settled"
        });
    }

    private Task<DisbursementWebhookResult> HandlePayoutFailed(Event stripeEvent)
    {
        var payout = stripeEvent.Data.Object as Payout;
        if (payout == null)
            return Task.FromResult(new DisbursementWebhookResult { Handled = false });

        _logger.LogWarning("Payout failed {PayoutId}: {FailureCode} - {FailureMessage}",
            payout.Id, payout.FailureCode, payout.FailureMessage);

        return Task.FromResult(new DisbursementWebhookResult
        {
            Handled = true,
            EventType = "payout_failed",
            TransferId = payout.Id,
            TenantId = payout.Metadata?.GetValueOrDefault(TenantMetadataKey),
            Amount = payout.Amount / 100m,
            Status = "failed",
            FailureCode = payout.FailureCode,
            FailureMessage = payout.FailureMessage
        });
    }

    private static StripeTransferResult MapToResult(Transfer transfer)
    {
        return new StripeTransferResult
        {
            TransferId = transfer.Id,
            Status = transfer.Reversed ? "reversed" : "created",
            Amount = transfer.Amount / 100m,
            StatementNumber = transfer.Metadata.GetValueOrDefault("statement_number"),
            CreatedAt = transfer.Created
        };
    }
}

/// <summary>
/// Result of a Stripe Transfer operation
/// </summary>
public class StripeTransferResult
{
    public string? TransferId { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? StatementNumber { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Result from processing a Stripe webhook event for disbursements
/// </summary>
public class DisbursementWebhookResult
{
    public bool Handled { get; set; }
    public string? EventType { get; set; }
    public string? TransferId { get; set; }
    public string? StatementNumber { get; set; }

    /// <summary>The tenant from the signed event's metadata (<see cref="StripeConnectService.TenantMetadataKey"/>).</summary>
    public string? TenantId { get; set; }

    public decimal Amount { get; set; }
    public string? Status { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
}
