using System.Text.Json;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;

namespace PremiumBillingService.Services;

/// <summary>
/// Orchestrates EFT/ACH auto-drafts for premium invoices.
/// Coordinates between NACHA file generation, Stripe ACH, invoice tracking, and draft lifecycle.
/// </summary>
public interface IEftDraftService
{
    /// <summary>
    /// Initiate an EFT draft for a single invoice
    /// </summary>
    Task<EftDraft> InitiateDraftAsync(InitiateEftDraftRequest request);

    /// <summary>
    /// Initiate EFT drafts for a batch of invoices (from billing run or invoice list)
    /// </summary>
    Task<BatchEftResult> InitiateBatchDraftAsync(InitiateBatchEftRequest request);

    /// <summary>
    /// Generate a NACHA file for all pending NACHA drafts
    /// </summary>
    Task<NachaFileResult> GenerateNachaFileForPendingDraftsAsync();

    /// <summary>
    /// Process an ACH return (bank rejection)
    /// </summary>
    Task<EftDraft> ProcessAchReturnAsync(ProcessAchReturnRequest request);

    /// <summary>
    /// Mark a draft as settled (payment confirmed)
    /// </summary>
    Task<EftDraft> SettleDraftAsync(string draftId);

    /// <summary>
    /// Process Stripe webhook and update draft/invoice accordingly
    /// </summary>
    Task ProcessStripeWebhookAsync(string json, string stripeSignature);

    /// <summary>
    /// Get all drafts for an invoice
    /// </summary>
    Task<IEnumerable<EftDraft>> GetDraftsByInvoiceAsync(string invoiceId);

    /// <summary>
    /// Get draft by ID
    /// </summary>
    Task<EftDraft?> GetDraftByIdAsync(string draftId);

    /// <summary>
    /// Cancel a pending draft
    /// </summary>
    Task<EftDraft> CancelDraftAsync(string draftId);
}

public class EftDraftService : IEftDraftService
{
    private readonly IEftDraftRepository _draftRepository;
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly IBillingRunRepository _billingRunRepository;
    private readonly INachaFileService _nachaFileService;
    private readonly IStripeAchService _stripeAchService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EftDraftService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public EftDraftService(
        IEftDraftRepository draftRepository,
        IPremiumInvoiceRepository invoiceRepository,
        IBillingRunRepository billingRunRepository,
        INachaFileService nachaFileService,
        IStripeAchService stripeAchService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<EftDraftService> logger)
    {
        _draftRepository = draftRepository;
        _invoiceRepository = invoiceRepository;
        _billingRunRepository = billingRunRepository;
        _nachaFileService = nachaFileService;
        _stripeAchService = stripeAchService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<EftDraft> InitiateDraftAsync(InitiateEftDraftRequest request)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(request.InvoiceId)
            ?? throw new InvalidOperationException($"Invoice {request.InvoiceId} not found");

        if (invoice.Status == InvoiceStatus.Paid || invoice.Status == InvoiceStatus.Voided)
            throw new InvalidOperationException($"Cannot draft against {invoice.Status} invoice");

        if (invoice.BalanceDue <= 0)
            throw new InvalidOperationException("Invoice has no balance due");

        // Fetch sponsor bank account info
        var bankAccount = await FetchSponsorBankAccountAsync(invoice.GroupNumber);
        if (bankAccount == null || !bankAccount.EftEnabled)
            throw new InvalidOperationException($"EFT not enabled for sponsor {invoice.GroupNumber}");

        var amount = request.Amount ?? invoice.BalanceDue;
        var method = request.Method ?? bankAccount.PreferredMethod ?? EftMethod.Nacha;

        // Validate bank account info for chosen method
        ValidateBankAccountForMethod(bankAccount, method, invoice.GroupNumber);

        var draft = new EftDraft
        {
            InvoiceId = invoice.Id,
            InvoiceNumber = invoice.InvoiceNumber,
            GroupNumber = invoice.GroupNumber,
            Amount = amount,
            Method = method,
            Status = EftDraftStatus.Pending,
            RoutingNumberLast4 = bankAccount.RoutingNumberLast4,
            AccountNumberLast4 = bankAccount.AccountNumberLast4,
            InitiatedBy = request.InitiatedBy
        };

        // For Stripe ACH, initiate immediately
        if (method == EftMethod.StripeAch)
        {
            var result = await _stripeAchService.CreateAchDraftAsync(
                bankAccount.StripeCustomerId!,
                bankAccount.StripePaymentMethodId!,
                amount,
                invoice.InvoiceNumber,
                invoice.GroupNumber);

            if (result.Status == "failed")
            {
                draft.Status = EftDraftStatus.Failed;
                draft.ErrorMessage = result.ErrorMessage;
            }
            else
            {
                draft.StripePaymentIntentId = result.PaymentIntentId;
                draft.Status = EftDraftStatus.Submitted;
                draft.SubmittedAt = DateTime.UtcNow;
                draft.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(4); // ACH typically 3-5 business days via Stripe
            }
        }
        // For NACHA, draft stays Pending until a NACHA file is generated
        else
        {
            draft.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
        }

        draft = await _draftRepository.CreateAsync(draft);

        _logger.LogInformation(
            "Initiated {Method} EFT draft {DraftId} for invoice {InvoiceNumber}, amount ${Amount:N2}",
            method, draft.Id, invoice.InvoiceNumber, amount);

        return draft;
    }

    public async Task<BatchEftResult> InitiateBatchDraftAsync(InitiateBatchEftRequest request)
    {
        var result = new BatchEftResult();
        var invoiceIds = new List<string>(request.InvoiceIds);

        // If billing run specified, get all invoice IDs from it
        if (!string.IsNullOrEmpty(request.BillingRunId))
        {
            var billingRun = await _billingRunRepository.GetByIdAsync(request.BillingRunId)
                ?? throw new InvalidOperationException($"Billing run {request.BillingRunId} not found");
            invoiceIds.AddRange(billingRun.InvoiceIds);
        }

        // Deduplicate before counting
        var uniqueInvoiceIds = invoiceIds.Distinct().ToList();
        result.TotalInvoices = uniqueInvoiceIds.Count;

        // Separate NACHA entries (built as batch) from Stripe (initiated individually)
        var nachaEntries = new List<NachaEntryDetail>();
        var nachaDrafts = new List<EftDraft>();

        foreach (var invoiceId in uniqueInvoiceIds)
        {
            try
            {
                var invoice = await _invoiceRepository.GetByIdAsync(invoiceId);
                if (invoice == null || invoice.BalanceDue <= 0 ||
                    invoice.Status == InvoiceStatus.Paid || invoice.Status == InvoiceStatus.Voided)
                {
                    result.Skipped++;
                    continue;
                }

                var bankAccount = await FetchSponsorBankAccountAsync(invoice.GroupNumber);
                if (bankAccount == null || !bankAccount.EftEnabled)
                {
                    result.Skipped++;
                    continue;
                }

                var method = request.Method ?? bankAccount.PreferredMethod ?? EftMethod.Nacha;

                if (method == EftMethod.StripeAch)
                {
                    // Initiate individually via Stripe
                    var draft = await InitiateDraftAsync(new InitiateEftDraftRequest
                    {
                        InvoiceId = invoiceId,
                        Method = EftMethod.StripeAch,
                        InitiatedBy = request.InitiatedBy
                    });
                    result.DraftIds.Add(draft.Id);
                    result.DraftsInitiated++;
                    result.TotalAmount += draft.Amount;
                }
                else
                {
                    // Collect for NACHA batch
                    var draft = new EftDraft
                    {
                        InvoiceId = invoice.Id,
                        InvoiceNumber = invoice.InvoiceNumber,
                        GroupNumber = invoice.GroupNumber,
                        Amount = invoice.BalanceDue,
                        Method = EftMethod.Nacha,
                        Status = EftDraftStatus.Pending,
                        RoutingNumberLast4 = bankAccount.RoutingNumberLast4,
                        AccountNumberLast4 = bankAccount.AccountNumberLast4,
                        InitiatedBy = request.InitiatedBy
                    };
                    draft = await _draftRepository.CreateAsync(draft);
                    nachaDrafts.Add(draft);

                    nachaEntries.Add(new NachaEntryDetail
                    {
                        RoutingNumber = bankAccount.RoutingNumber!,
                        AccountNumber = bankAccount.AccountNumber!,
                        AccountType = bankAccount.AccountType,
                        Amount = invoice.BalanceDue,
                        GroupNumber = invoice.GroupNumber,
                        IndividualName = bankAccount.AccountHolderName ?? invoice.SponsorName,
                        IndividualId = invoice.GroupNumber
                    });

                    result.DraftIds.Add(draft.Id);
                    result.DraftsInitiated++;
                    result.TotalAmount += invoice.BalanceDue;
                }
            }
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorMessages.Add($"Invoice {invoiceId}: {ex.Message}");
                _logger.LogWarning(ex, "Failed to initiate EFT draft for invoice {InvoiceId}", invoiceId);
            }
        }

        // Generate NACHA file if there are NACHA entries
        if (nachaEntries.Count > 0)
        {
            var nachaOptions = BuildNachaOptionsFromConfig();
            var nachaResult = _nachaFileService.GenerateNachaFile(nachaEntries, nachaOptions);
            result.NachaFile = nachaResult;

            // Update NACHA drafts with file reference, trace numbers, and mark as submitted
            for (int i = 0; i < nachaDrafts.Count; i++)
            {
                var draft = nachaDrafts[i];
                draft.NachaFileReference = nachaResult.FileReference;
                draft.Status = EftDraftStatus.Submitted;
                draft.SubmittedAt = DateTime.UtcNow;
                draft.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
                draft.TraceNumber = nachaEntries[i].TraceNumber;
                await _draftRepository.UpdateAsync(draft);
            }
        }

        _logger.LogInformation(
            "Batch EFT: {Initiated}/{Total} drafts initiated, {Skipped} skipped, {Errors} errors, ${Amount:N2} total",
            result.DraftsInitiated, result.TotalInvoices, result.Skipped, result.Errors, result.TotalAmount);

        return result;
    }

    public async Task<NachaFileResult> GenerateNachaFileForPendingDraftsAsync()
    {
        var pendingDrafts = (await _draftRepository.GetByStatusAsync(EftDraftStatus.Pending))
            .Where(d => d.Method == EftMethod.Nacha)
            .ToList();

        if (pendingDrafts.Count == 0)
            throw new InvalidOperationException("No pending NACHA drafts to process");

        var entries = new List<NachaEntryDetail>();
        var includedDrafts = new List<EftDraft>();
        var skippedDraftIds = new HashSet<string>();

        foreach (var draft in pendingDrafts)
        {
            var bankAccount = await FetchSponsorBankAccountAsync(draft.GroupNumber);
            if (bankAccount?.RoutingNumber == null || bankAccount.AccountNumber == null)
            {
                _logger.LogWarning("Skipping draft {DraftId}: missing bank account for group {GroupNumber}",
                    draft.Id, draft.GroupNumber);
                skippedDraftIds.Add(draft.Id);
                continue;
            }

            entries.Add(new NachaEntryDetail
            {
                RoutingNumber = bankAccount.RoutingNumber,
                AccountNumber = bankAccount.AccountNumber,
                AccountType = bankAccount.AccountType,
                Amount = draft.Amount,
                GroupNumber = draft.GroupNumber,
                IndividualName = bankAccount.AccountHolderName ?? draft.GroupNumber,
                IndividualId = draft.GroupNumber
            });
            includedDrafts.Add(draft);
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("No drafts with valid bank accounts to include in NACHA file");

        var nachaOptions = BuildNachaOptionsFromConfig();
        var result = _nachaFileService.GenerateNachaFile(entries, nachaOptions);

        // Only mark drafts that were actually included in the file as submitted
        for (int i = 0; i < includedDrafts.Count; i++)
        {
            var draft = includedDrafts[i];
            draft.NachaFileReference = result.FileReference;
            draft.Status = EftDraftStatus.Submitted;
            draft.SubmittedAt = DateTime.UtcNow;
            draft.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
            draft.TraceNumber = entries[i].TraceNumber;
            await _draftRepository.UpdateAsync(draft);
        }

        return result;
    }

    public async Task<EftDraft> ProcessAchReturnAsync(ProcessAchReturnRequest request)
    {
        var draft = await _draftRepository.GetByIdAsync(request.DraftId)
            ?? throw new InvalidOperationException($"Draft {request.DraftId} not found");

        if (draft.Status != EftDraftStatus.Submitted && draft.Status != EftDraftStatus.Processing)
            throw new InvalidOperationException($"Cannot process return for draft in {draft.Status} state");

        draft.Status = EftDraftStatus.Returned;
        draft.ReturnCode = request.ReturnCode;
        draft.ReturnReason = request.ReturnReason ?? MapReturnCodeToReason(request.ReturnCode);
        draft.ReturnedAt = DateTime.UtcNow;

        await _draftRepository.UpdateAsync(draft);

        // Reverse the payment on the invoice if one was recorded
        var invoice = await _invoiceRepository.GetByIdAsync(draft.InvoiceId);
        if (invoice != null)
        {
            // Add a negative adjustment for the returned draft
            invoice.Adjustments.Add(new InvoiceAdjustment
            {
                Type = AdjustmentType.Other,
                Description = $"ACH return ({draft.ReturnCode}): {draft.ReturnReason}",
                Amount = 0, // Don't change the invoice total; the payment reversal handles balance
                AdjustmentDate = DateTime.UtcNow
            });

            // Remove the payment that was recorded for this draft
            var draftPayment = invoice.Payments.FirstOrDefault(p =>
                p.ReferenceNumber == draft.TraceNumber || p.ReferenceNumber == draft.StripePaymentIntentId);
            if (draftPayment != null)
            {
                invoice.Payments.Remove(draftPayment);
            }

            invoice.RecalculateTotals();

            // Update invoice status
            if (invoice.BalanceDue > 0 && invoice.TotalPaid > 0)
                invoice.Status = InvoiceStatus.PartiallyPaid;
            else if (invoice.BalanceDue > 0)
                invoice.Status = invoice.DueDate < DateTime.UtcNow ? InvoiceStatus.Overdue : InvoiceStatus.Sent;

            await _invoiceRepository.UpdateAsync(invoice);
        }

        _logger.LogWarning(
            "ACH return processed for draft {DraftId}, invoice {InvoiceNumber}: {ReturnCode} - {ReturnReason}",
            draft.Id, draft.InvoiceNumber, SanitizeForLog(draft.ReturnCode), SanitizeForLog(draft.ReturnReason));

        // Check if auto-retry is appropriate
        if (ShouldRetry(draft))
        {
            _logger.LogInformation("Auto-retry eligible for draft {DraftId} (attempt {RetryCount}/{MaxRetries})",
                draft.Id, draft.RetryCount + 1, draft.MaxRetries);
        }

        return draft;
    }

    public async Task<EftDraft> SettleDraftAsync(string draftId)
    {
        var draft = await _draftRepository.GetByIdAsync(draftId)
            ?? throw new InvalidOperationException($"Draft {draftId} not found");

        if (draft.Status != EftDraftStatus.Submitted && draft.Status != EftDraftStatus.Processing)
            throw new InvalidOperationException($"Cannot settle draft in {draft.Status} state");

        draft.Status = EftDraftStatus.Settled;
        draft.SettledAt = DateTime.UtcNow;
        await _draftRepository.UpdateAsync(draft);

        // Record payment on the invoice
        var invoice = await _invoiceRepository.GetByIdAsync(draft.InvoiceId);
        if (invoice != null)
        {
            var payment = new InvoicePayment
            {
                Amount = draft.Amount,
                PaymentDate = DateTime.UtcNow,
                PaymentMethod = draft.Method == EftMethod.StripeAch ? "StripeACH" : "ACH",
                ReferenceNumber = draft.TraceNumber ?? draft.StripePaymentIntentId,
                ReceivedDate = DateTime.UtcNow
            };

            invoice.Payments.Add(payment);
            invoice.RecalculateTotals();

            if (invoice.BalanceDue <= 0)
                invoice.Status = InvoiceStatus.Paid;
            else if (invoice.TotalPaid > 0)
                invoice.Status = InvoiceStatus.PartiallyPaid;

            await _invoiceRepository.UpdateAsync(invoice);
        }

        _logger.LogInformation("Draft {DraftId} settled for invoice {InvoiceNumber}, amount ${Amount:N2}",
            draft.Id, draft.InvoiceNumber, draft.Amount);

        return draft;
    }

    public async Task ProcessStripeWebhookAsync(string json, string stripeSignature)
    {
        var webhookResult = await _stripeAchService.ProcessWebhookAsync(json, stripeSignature);

        if (!webhookResult.Handled || string.IsNullOrEmpty(webhookResult.PaymentIntentId))
            return;

        // Find the draft by Stripe PaymentIntent ID
        var drafts = await _draftRepository.GetByStripePaymentIntentIdAsync(webhookResult.PaymentIntentId);
        var draft = drafts.FirstOrDefault();

        if (draft == null)
        {
            _logger.LogWarning("No draft found for PaymentIntent {PaymentIntentId}", webhookResult.PaymentIntentId);
            return;
        }

        switch (webhookResult.EventType)
        {
            case "payment_succeeded":
                await SettleDraftAsync(draft.Id);
                break;

            case "payment_failed":
                await ProcessAchReturnAsync(new ProcessAchReturnRequest
                {
                    DraftId = draft.Id,
                    ReturnCode = webhookResult.FailureCode ?? "STRIPE_FAIL",
                    ReturnReason = webhookResult.FailureMessage
                });
                break;

            case "payment_cancelled":
                draft.Status = EftDraftStatus.Cancelled;
                await _draftRepository.UpdateAsync(draft);
                break;
        }
    }

    public async Task<IEnumerable<EftDraft>> GetDraftsByInvoiceAsync(string invoiceId)
    {
        return await _draftRepository.GetByInvoiceIdAsync(invoiceId);
    }

    public async Task<EftDraft?> GetDraftByIdAsync(string draftId)
    {
        return await _draftRepository.GetByIdAsync(draftId);
    }

    public async Task<EftDraft> CancelDraftAsync(string draftId)
    {
        var draft = await _draftRepository.GetByIdAsync(draftId)
            ?? throw new InvalidOperationException($"Draft {draftId} not found");

        if (draft.Status != EftDraftStatus.Pending)
            throw new InvalidOperationException($"Can only cancel Pending drafts, current: {draft.Status}");

        // If Stripe, cancel the PaymentIntent
        if (draft.Method == EftMethod.StripeAch && !string.IsNullOrEmpty(draft.StripePaymentIntentId))
        {
            await _stripeAchService.CancelDraftAsync(draft.StripePaymentIntentId);
        }

        draft.Status = EftDraftStatus.Cancelled;
        return await _draftRepository.UpdateAsync(draft);
    }

    // --- Private helpers ---

    private async Task<SponsorBankAccount?> FetchSponsorBankAccountAsync(string groupNumber)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SponsorService");
            var response = await client.GetAsync($"/api/v1/sponsors/{groupNumber}/bank-account");
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<SponsorBankAccount>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch bank account for group {GroupNumber}", groupNumber);
            return null;
        }
    }

    private static void ValidateBankAccountForMethod(SponsorBankAccount bankAccount, EftMethod method, string groupNumber)
    {
        if (method == EftMethod.Nacha)
        {
            if (string.IsNullOrEmpty(bankAccount.RoutingNumber) || string.IsNullOrEmpty(bankAccount.AccountNumber))
                throw new InvalidOperationException(
                    $"NACHA draft requires routing and account numbers for sponsor {groupNumber}");
        }
        else if (method == EftMethod.StripeAch)
        {
            if (string.IsNullOrEmpty(bankAccount.StripeCustomerId) || string.IsNullOrEmpty(bankAccount.StripePaymentMethodId))
                throw new InvalidOperationException(
                    $"Stripe ACH draft requires Stripe customer and payment method for sponsor {groupNumber}");
        }
    }

    private NachaFileOptions BuildNachaOptionsFromConfig()
    {
        return new NachaFileOptions
        {
            ImmediateDestination = _configuration["Nacha:ImmediateDestination"] ?? "",
            ImmediateOrigin = _configuration["Nacha:ImmediateOrigin"] ?? "",
            ImmediateDestinationName = _configuration["Nacha:ImmediateDestinationName"] ?? "",
            ImmediateOriginName = _configuration["Nacha:ImmediateOriginName"] ?? "",
            CompanyName = _configuration["Nacha:CompanyName"] ?? "",
            CompanyId = _configuration["Nacha:CompanyId"] ?? "",
            OriginatingDfi = long.TryParse(_configuration["Nacha:OriginatingDfi"], out var dfi) ? dfi : 0,
            CompanyEntryDescription = _configuration["Nacha:CompanyEntryDescription"] ?? "PREMIUM"
        };
    }

    private static bool ShouldRetry(EftDraft draft)
    {
        if (draft.RetryCount >= draft.MaxRetries)
            return false;

        // Don't retry for account closed, unauthorized, or invalid account
        var nonRetryableCodes = new[] { "R02", "R03", "R04", "R07", "R10", "R16", "R20" };
        return !nonRetryableCodes.Contains(draft.ReturnCode);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private static string MapReturnCodeToReason(string returnCode)
    {
        return returnCode switch
        {
            "R01" => "Insufficient Funds",
            "R02" => "Account Closed",
            "R03" => "No Account/Unable to Locate Account",
            "R04" => "Invalid Account Number",
            "R05" => "Unauthorized Debit to Consumer Account",
            "R06" => "Returned per ODFI Request",
            "R07" => "Authorization Revoked by Customer",
            "R08" => "Payment Stopped",
            "R09" => "Uncollected Funds",
            "R10" => "Customer Advises Not Authorized",
            "R16" => "Account Frozen",
            "R20" => "Non-Transaction Account",
            "R29" => "Corporate Customer Advises Not Authorized",
            _ => $"ACH Return Code {returnCode}"
        };
    }
}
