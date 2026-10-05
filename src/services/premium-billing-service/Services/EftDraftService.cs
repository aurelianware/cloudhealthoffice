using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NachaTransmission;
using PremiumBillingService.Clients;
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
    /// Generate a NACHA file for all pending NACHA drafts and send it to the
    /// bank. Returns the masked summary and receipt, never the file.
    /// </summary>
    Task<NachaFileResult> GenerateNachaFileForPendingDraftsAsync();

    /// <summary>NACHA files of this tenant that were not delivered (no content).</summary>
    Task<IReadOnlyList<NachaHeldFile>> ListHeldNachaFilesAsync();

    /// <summary>
    /// Re-sends a held NACHA file. The acting user must hold payments:approve
    /// (controller) and must not be the user who released it.
    /// </summary>
    Task<NachaFileResult> RetryNachaTransmissionAsync(string fileReference);

    /// <summary>
    /// A held NACHA file for a platform admin (controller: platform:admin),
    /// never the releasing user. Audited with the reason.
    /// </summary>
    Task<NachaRetrievedFile> RetrieveHeldNachaFileAsync(string fileReference, string reason);

    /// <summary>
    /// Records what the bank said about a NACHA file whose delivery was unknown
    /// (payments:approve in the controller; a user, not the releaser). Received:
    /// its drafts are Submitted. Not received: they can be retried or retrieved
    /// again (or, when the held file expired, go back to Pending).
    /// </summary>
    Task<NachaDeliveryResolutionResult> ResolveNachaDeliveryAsync(string fileReference, bool bankReceived, string reason);

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

/// <summary>
/// Every draft this release would send is already being released by another
/// request (409): two releases never put the same draft in two files.
/// </summary>
public sealed class NachaReleaseConflictException : Exception
{
    public NachaReleaseConflictException(string message) : base(message) { }
}

public class EftDraftService : IEftDraftService
{
    private readonly IEftDraftRepository _draftRepository;
    private readonly IPremiumInvoiceRepository _invoiceRepository;
    private readonly IBillingRunRepository _billingRunRepository;
    private readonly INachaFileService _nachaFileService;
    private readonly IStripeAchService _stripeAchService;
    private readonly ISponsorBankAccountSource _bankAccounts;
    private readonly INachaDispatcher _dispatcher;
    private readonly ICurrentActor _actor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EftDraftService> _logger;
    private readonly DebitSeparationOfDuties _separationOfDuties;

    /// <summary>Recorded as the actor for changes driven by (signature-verified) Stripe events.</summary>
    public const string StripeWebhookActor = "stripe-webhook";

    public EftDraftService(
        IEftDraftRepository draftRepository,
        IPremiumInvoiceRepository invoiceRepository,
        IBillingRunRepository billingRunRepository,
        INachaFileService nachaFileService,
        IStripeAchService stripeAchService,
        ISponsorBankAccountSource bankAccounts,
        INachaDispatcher dispatcher,
        ICurrentActor actor,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration,
        ILogger<EftDraftService> logger)
    {
        _draftRepository = draftRepository;
        _invoiceRepository = invoiceRepository;
        _billingRunRepository = billingRunRepository;
        _nachaFileService = nachaFileService;
        _stripeAchService = stripeAchService;
        _bankAccounts = bankAccounts;
        _dispatcher = dispatcher;
        _actor = actor;
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
        _logger = logger;
        _separationOfDuties = new DebitSeparationOfDuties(billingRunRepository, actor, logger);
    }

    /// <summary>The acting user from the token; never from a request body.</summary>
    private string ActorId => _actor.UserId;

    public async Task<EftDraft> InitiateDraftAsync(InitiateEftDraftRequest request)
    {
        var invoice = await _invoiceRepository.GetByIdAsync(request.InvoiceId)
            ?? throw new InvalidOperationException($"Invoice {request.InvoiceId} not found");

        if (invoice.Status == InvoiceStatus.Paid || invoice.Status == InvoiceStatus.Voided)
            throw new InvalidOperationException($"Cannot draft against {invoice.Status} invoice");

        if (invoice.BalanceDue <= 0)
            throw new InvalidOperationException("Invoice has no balance due");

        // The initiator comes from the token (any body value is ignored), and may
        // not be the user who prepared the invoice (maker-checker).
        request.InitiatedBy = ActorId;
        await _separationOfDuties.EnsureMayReleaseAsync(new[] { invoice });

        // Fetch sponsor bank account info
        var lookup = await _bankAccounts.GetAsync(invoice.TenantId, invoice.GroupNumber);
        if (lookup.Status == SponsorBankAccountLookupStatus.Unavailable)
        {
            _logger.LogError("EFT draft for invoice {InvoiceNumber} (group {GroupNumber}) needs attention: {Reason}",
                invoice.InvoiceNumber, invoice.GroupNumber, lookup.Reason);
            throw new InvalidOperationException($"Sponsor {invoice.GroupNumber}: {lookup.Reason}");
        }
        if (lookup.Status == SponsorBankAccountLookupStatus.NotEnrolled || lookup.Account == null)
            throw new InvalidOperationException($"EFT not enabled for sponsor {invoice.GroupNumber}");
        var bankAccount = lookup.Account;

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
        request.InitiatedBy = ActorId;

        // Maker-checker over the whole batch before any money moves: one invoice
        // the actor prepared refuses the batch.
        var invoices = new Dictionary<string, PremiumInvoice?>(StringComparer.Ordinal);
        foreach (var invoiceId in uniqueInvoiceIds)
            invoices[invoiceId] = await _invoiceRepository.GetByIdAsync(invoiceId);
        await _separationOfDuties.EnsureMayReleaseAsync(invoices.Values.OfType<PremiumInvoice>());

        // Separate NACHA entries (built as batch) from Stripe (initiated individually)
        var nachaEntries = new List<NachaEntryDetail>();
        var nachaDrafts = new List<EftDraft>();
        // The NACHA drafts this batch creates are born claimed by it (Releasing),
        // so a concurrent release of Pending drafts never puts them in a second file.
        var claimId = NewClaimId();
        var claimedAt = DateTime.UtcNow;

        foreach (var invoiceId in uniqueInvoiceIds)
        {
            try
            {
                var invoice = invoices[invoiceId];
                if (invoice == null || invoice.BalanceDue <= 0 ||
                    invoice.Status == InvoiceStatus.Paid || invoice.Status == InvoiceStatus.Voided)
                {
                    result.Skipped++;
                    continue;
                }

                var lookup = await _bankAccounts.GetAsync(invoice.TenantId, invoice.GroupNumber);
                if (lookup.Status == SponsorBankAccountLookupStatus.Unavailable)
                {
                    // Not knowing the bank details is not the same as "not enrolled":
                    // the item needs attention, it is not a normal skip.
                    result.Errors++;
                    result.ErrorMessages.Add($"Invoice {invoiceId}: {lookup.Reason}");
                    result.NeedsAttention.Add(new EftAttentionItem
                    {
                        InvoiceId = invoice.Id,
                        GroupNumber = invoice.GroupNumber,
                        Reason = lookup.Reason ?? "Sponsor bank details unavailable"
                    });
                    _logger.LogError("EFT draft for invoice {InvoiceNumber} (group {GroupNumber}) needs attention: {Reason}",
                        invoice.InvoiceNumber, invoice.GroupNumber, lookup.Reason);
                    continue;
                }
                if (lookup.Status == SponsorBankAccountLookupStatus.NotEnrolled || lookup.Account == null)
                {
                    result.Skipped++;
                    continue;
                }
                var bankAccount = lookup.Account;

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
                        Status = EftDraftStatus.Releasing,
                        ReleaseClaimId = claimId,
                        ReleaseClaimedAt = claimedAt,
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

        // Build the NACHA file and send it straight to the bank. The drafts are
        // Submitted only once the bank has it; otherwise they await retrieval
        // (file held encrypted) or stay Pending (nothing held).
        if (nachaEntries.Count > 0)
        {
            result.NachaFile = await SendNachaFileAsync(nachaEntries, nachaDrafts, request.BillingRunId, claimId);
        }

        // Mark the billing run so the items needing attention are visible on it.
        if (result.NeedsAttention.Count > 0 && !string.IsNullOrEmpty(request.BillingRunId))
        {
            var run = await _billingRunRepository.GetByIdAsync(request.BillingRunId);
            if (run != null)
            {
                foreach (var item in result.NeedsAttention)
                    run.Warnings.Add($"EFT needs attention for invoice {item.InvoiceId} (group {item.GroupNumber}): {item.Reason}");
                await _billingRunRepository.UpdateAsync(run);
            }
        }

        _logger.LogInformation(
            "Batch EFT: {Initiated}/{Total} drafts initiated, {Skipped} skipped, {Errors} errors, ${Amount:N2} total",
            result.DraftsInitiated, result.TotalInvoices, result.Skipped, result.Errors, result.TotalAmount);

        return result;
    }

    public async Task<NachaFileResult> GenerateNachaFileForPendingDraftsAsync()
    {
        if (_actor.IsService)
            throw new SeparationOfDutiesException(
                "Separation of duties: releasing sponsor debits needs a user with payments:approve, not a service token");

        var pendingDrafts = (await _draftRepository.GetByStatusAsync(EftDraftStatus.Pending))
            .Where(d => d.Method == EftMethod.Nacha)
            .ToList();

        if (pendingDrafts.Count == 0)
            throw new InvalidOperationException("No pending NACHA drafts to process");

        // Claim the drafts before anything is built: Pending to Releasing, one
        // conditional write each. A concurrent release gets none of these, so the
        // same draft can never be in two files at the bank.
        var claimId = NewClaimId();
        var claimedAt = DateTime.UtcNow;
        var claimed = new List<EftDraft>();
        foreach (var draft in pendingDrafts)
        {
            if (!await _draftRepository.TryClaimForReleaseAsync(draft.Id, claimId, claimedAt))
                continue;
            draft.Status = EftDraftStatus.Releasing;
            draft.ReleaseClaimId = claimId;
            draft.ReleaseClaimedAt = claimedAt;
            claimed.Add(draft);
        }

        if (claimed.Count == 0)
            throw new NachaReleaseConflictException(
                "The pending NACHA drafts are already being released by another request; nothing was sent.");

        var entries = new List<NachaEntryDetail>();
        var includedDrafts = new List<EftDraft>();
        var needsAttention = new List<EftAttentionItem>();

        try
        {
            foreach (var draft in claimed)
            {
                var lookup = await _bankAccounts.GetAsync(draft.TenantId, draft.GroupNumber);
                var bankAccount = lookup.Account;
                if (lookup.Status != SponsorBankAccountLookupStatus.Found
                    || bankAccount?.RoutingNumber == null || bankAccount.AccountNumber == null)
                {
                    var reason = lookup.Status switch
                    {
                        SponsorBankAccountLookupStatus.Unavailable => lookup.Reason ?? "Sponsor bank details unavailable",
                        SponsorBankAccountLookupStatus.NotEnrolled => "Sponsor is no longer enrolled in auto-debit",
                        _ => "Sponsor bank account is missing routing or account number"
                    };
                    // Back to Pending (so it is picked up once fixed) and reported, never silently dropped.
                    await ReleaseClaimsAsync(new[] { draft }, claimId, reason);
                    _logger.LogError("Draft {DraftId} for group {GroupNumber} left out of the NACHA file and needs attention: {Reason}",
                        draft.Id, draft.GroupNumber, reason);
                    needsAttention.Add(new EftAttentionItem { DraftId = draft.Id, InvoiceId = draft.InvoiceId, GroupNumber = draft.GroupNumber, Reason = reason });
                    continue;
                }

                // The masked summary shows the last 4 of the account actually debited.
                draft.RoutingNumberLast4 = bankAccount.RoutingNumberLast4 ?? Last4(bankAccount.RoutingNumber);
                draft.AccountNumberLast4 = bankAccount.AccountNumberLast4 ?? Last4(bankAccount.AccountNumber);
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
        }
        catch
        {
            // Nothing was built or sent: every draft this release still holds goes back.
            await ReleaseClaimsAsync(claimed.Where(d => d.Status == EftDraftStatus.Releasing), claimId,
                "The NACHA release failed before a file was built; back to Pending for the next release.");
            throw;
        }

        if (entries.Count == 0)
            throw new InvalidOperationException(
                "No drafts with valid bank accounts to include in NACHA file; needs attention: " +
                string.Join("; ", needsAttention.Select(a => $"draft {a.DraftId} (group {a.GroupNumber}): {a.Reason}")));

        // Only drafts actually in the file change state, and only to what the
        // transmission outcome says.
        var result = await SendNachaFileAsync(entries, includedDrafts, runId: null, claimId);
        result.NeedsAttention.AddRange(needsAttention);
        return result;
    }

    private static string NewClaimId() => Guid.NewGuid().ToString("N");

    /// <summary>Releasing (under this claim) back to Pending: nothing of theirs was sent.</summary>
    private async Task ReleaseClaimsAsync(IEnumerable<EftDraft> drafts, string claimId, string reason)
    {
        foreach (var draft in drafts.ToList())
        {
            try
            {
                await _draftRepository.ReleaseClaimAsync(draft.Id, claimId, reason);
                draft.Status = EftDraftStatus.Pending;
                draft.ReleaseClaimId = null;
                draft.ReleaseClaimedAt = null;
                draft.ErrorMessage = reason;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Draft {DraftId} could not be released from NACHA release {ClaimId}; it stays Releasing and needs attention",
                    draft.Id, claimId);
            }
        }
    }

    /// <summary>
    /// Generates the file, hands it to the dispatcher (bank SFTP, or held
    /// encrypted for retrieval), updates the drafts to match what happened, and
    /// returns the masked summary. The file content never leaves this method.
    /// The drafts are held by <paramref name="claimId"/> (Releasing) throughout.
    /// </summary>
    private async Task<NachaFileResult> SendNachaFileAsync(List<NachaEntryDetail> entries, List<EftDraft> drafts, string? runId, string claimId)
    {
        GeneratedNachaFile file;
        NachaFileFacts facts;
        try
        {
            file = _nachaFileService.GenerateNachaFile(entries, BuildNachaOptionsFromConfig());
            facts = NachaFileFacts.From(file.FileContent);
        }
        catch
        {
            await ReleaseClaimsAsync(drafts, claimId,
                "The NACHA file could not be built; nothing was sent. Back to Pending for the next release.");
            throw;
        }

        NachaDispatchOutcome outcome;
        try
        {
            outcome = await _dispatcher.DispatchAsync(new NachaTransmissionRequest
            {
                TenantId = _actor.TenantId,
                FileReference = file.FileReference,
                FileName = file.FileName,
                Content = file.FileContent,
                RunId = runId,
                BatchId = file.FileReference,
                TransmittedBy = ActorId,
            });
        }
        catch (Exception ex)
        {
            // The dispatcher answers every transmission failure with an outcome; an
            // exception here means it is not known whether the bank got the file.
            // The drafts stay Releasing (never back to Pending, which could send
            // them again) until someone checks with the bank.
            _logger.LogCritical(ex,
                "NACHA file {FileReference} (release {ClaimId}): delivery to the bank is unknown; its {Count} drafts stay " +
                "Releasing and must be checked with the bank before anything is re-sent",
                file.FileReference, claimId, drafts.Count);
            throw;
        }
        finally
        {
            file.FileContent = string.Empty;
        }

        var now = DateTime.UtcNow;
        var result = new NachaFileResult
        {
            FileReference = file.FileReference,
            FileName = file.FileName,
            EntryCount = file.EntryCount,
            TotalAmount = file.TotalAmount,
            TotalDebitAmount = facts.TotalDebitAmount,
            TotalCreditAmount = facts.TotalCreditAmount,
            GeneratedAt = file.GeneratedAt,
            TransmissionStatus = outcome.Status.ToString(),
            TransmissionError = outcome.Reason,
            HeldUntil = outcome.HeldUntil,
            Receipt = outcome.Receipt,
        };

        for (int i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            draft.LastUpdatedBy = ActorId;
            switch (outcome.Status)
            {
                case NachaTransmissionStatus.Transmitted:
                    draft.NachaFileReference = file.FileReference;
                    draft.TraceNumber = entries[i].TraceNumber;
                    draft.Status = EftDraftStatus.Submitted;
                    draft.SubmittedAt = now;
                    draft.ExpectedSettlementDate = now.AddBusinessDays(2);
                    draft.ErrorMessage = null;
                    break;
                case NachaTransmissionStatus.AwaitingRetrieval:
                    draft.NachaFileReference = file.FileReference;
                    draft.TraceNumber = entries[i].TraceNumber;
                    draft.Status = EftDraftStatus.AwaitingRetrieval;
                    draft.ErrorMessage = AwaitingRetrievalMessage(file.FileReference, outcome.Reason);
                    break;
                case NachaTransmissionStatus.DeliveryUnknown:
                    // It may be at the bank: never back to Pending (that would send it again).
                    draft.NachaFileReference = file.FileReference;
                    draft.TraceNumber = entries[i].TraceNumber;
                    draft.Status = EftDraftStatus.DeliveryUnknown;
                    draft.ErrorMessage = DeliveryUnknownMessage(file.FileReference, outcome.Reason);
                    break;
                default:
                    // Nothing was sent or held: the draft goes back to Pending for the next file.
                    draft.NachaFileReference = null;
                    draft.TraceNumber = null;
                    draft.Status = EftDraftStatus.Pending;
                    draft.ReleaseClaimId = null;
                    draft.ReleaseClaimedAt = null;
                    draft.ErrorMessage = outcome.Reason;
                    break;
            }
            await _draftRepository.UpdateAsync(draft);

            var summary = Summary(draft);
            summary.AccountHolderName = entries[i].IndividualName;
            result.Entries.Add(summary);
        }

        return result;
    }

    private static string DeliveryUnknownMessage(string fileReference, string? reason)
        => $"NACHA file {fileReference} may have reached the bank: {reason} Do not send it again: verify with the bank, then " +
           "another user with payments:approve records whether the bank received it.";

    private static string AwaitingRetrievalMessage(string fileReference, string? reason)
        => $"NACHA file {fileReference} was not delivered to the bank: {reason} It is held encrypted for 7 days: " +
           "a platform admin must retrieve it, or another user with payments:approve must retry it.";

    public Task<IReadOnlyList<NachaHeldFile>> ListHeldNachaFilesAsync()
        => _dispatcher.ListHeldAsync(_actor.TenantId);

    public async Task<NachaFileResult> RetryNachaTransmissionAsync(string fileReference)
    {
        var tenantId = _actor.TenantId;
        var drafts = (await _draftRepository.GetByStatusAsync(EftDraftStatus.AwaitingRetrieval))
            .Where(d => d.NachaFileReference == fileReference)
            .ToList();

        NachaDispatchOutcome outcome;
        try
        {
            outcome = await _dispatcher.RetryAsync(tenantId, fileReference, new NachaActor(ActorId, _actor.IsService));
        }
        catch (Exception ex) when (ex is NachaHeldFileExpiredException or NachaHeldFileNotFoundException)
        {
            // The held file is gone: its drafts go back to Pending, so the next
            // release builds a new file for them.
            foreach (var draft in drafts)
            {
                draft.Status = EftDraftStatus.Pending;
                draft.NachaFileReference = null;
                draft.TraceNumber = null;
                draft.ErrorMessage = $"Held NACHA file {fileReference} expired before it was delivered; back to Pending for the next file.";
                draft.LastUpdatedBy = ActorId;
                await _draftRepository.UpdateAsync(draft);
            }
            throw;
        }

        var held = await _dispatcher.GetHeldAsync(tenantId, fileReference);
        var result = new NachaFileResult
        {
            FileReference = fileReference,
            FileName = held?.FileName ?? string.Empty,
            EntryCount = held?.EntryCount ?? drafts.Count,
            TotalAmount = drafts.Sum(d => d.Amount),
            TotalDebitAmount = held?.TotalDebitAmount ?? 0,
            TotalCreditAmount = held?.TotalCreditAmount ?? 0,
            GeneratedAt = held?.CreatedAt ?? DateTime.UtcNow,
            TransmissionStatus = outcome.Status.ToString(),
            TransmissionError = outcome.Reason,
            HeldUntil = outcome.HeldUntil,
            Receipt = outcome.Receipt,
        };

        var now = DateTime.UtcNow;
        foreach (var draft in drafts)
        {
            if (outcome.Status == NachaTransmissionStatus.Transmitted)
            {
                draft.Status = EftDraftStatus.Submitted;
                draft.SubmittedAt = now;
                draft.ExpectedSettlementDate = now.AddBusinessDays(2);
                draft.ErrorMessage = null;
                draft.LastUpdatedBy = ActorId;
                await _draftRepository.UpdateAsync(draft);
            }
            else if (outcome.Status == NachaTransmissionStatus.DeliveryUnknown)
            {
                draft.Status = EftDraftStatus.DeliveryUnknown;
                draft.ErrorMessage = DeliveryUnknownMessage(fileReference, outcome.Reason);
                draft.LastUpdatedBy = ActorId;
                await _draftRepository.UpdateAsync(draft);
            }
            result.Entries.Add(Summary(draft));
        }

        return result;
    }

    public async Task<NachaDeliveryResolutionResult> ResolveNachaDeliveryAsync(string fileReference, bool bankReceived, string reason)
    {
        if (_actor.IsService)
            throw new SeparationOfDutiesException(
                "Separation of duties: recording whether the bank received a NACHA file needs a user with payments:approve, not a service token");

        var drafts = (await _draftRepository.GetByStatusAsync(EftDraftStatus.DeliveryUnknown))
            .Where(d => d.NachaFileReference == fileReference)
            .ToList();

        var fileStillHeld = true;
        try
        {
            // Checks the user (not a service, not the releaser), the reason and the
            // file's state, and records the answer on the held file.
            await _dispatcher.ResolveDeliveryUnknownAsync(_actor.TenantId, fileReference, new NachaActor(ActorId, _actor.IsService), bankReceived, reason);
        }
        catch (Exception ex) when ((ex is NachaHeldFileExpiredException or NachaHeldFileNotFoundException) && drafts.Count > 0)
        {
            // The held file is gone (7 days); the drafts still wait for the answer.
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason (what the bank said) is required.");
            if (drafts.Any(d => string.Equals(d.LastUpdatedBy, ActorId, StringComparison.OrdinalIgnoreCase)))
                throw new SeparationOfDutiesException(
                    "Separation of duties: you released this NACHA file, so you cannot record whether the bank received it.");
            fileStillHeld = false;
        }

        var now = DateTime.UtcNow;
        var status = bankReceived ? EftDraftStatus.Submitted
            : fileStillHeld ? EftDraftStatus.AwaitingRetrieval : EftDraftStatus.Pending;
        foreach (var draft in drafts)
        {
            draft.Status = status;
            draft.LastUpdatedBy = ActorId;
            switch (status)
            {
                case EftDraftStatus.Submitted:
                    draft.SubmittedAt = now;
                    draft.ExpectedSettlementDate = now.AddBusinessDays(2);
                    draft.ErrorMessage = $"The bank confirmed it received NACHA file {fileReference} (recorded by {ActorId}).";
                    break;
                case EftDraftStatus.AwaitingRetrieval:
                    draft.ErrorMessage = AwaitingRetrievalMessage(fileReference, "The bank confirmed it did not receive it.");
                    break;
                default:
                    draft.NachaFileReference = null;
                    draft.TraceNumber = null;
                    draft.ReleaseClaimId = null;
                    draft.ReleaseClaimedAt = null;
                    draft.ErrorMessage = $"The bank confirmed it did not receive NACHA file {fileReference}, which has expired; back to Pending for the next file.";
                    break;
            }
            await _draftRepository.UpdateAsync(draft);
        }

        return new NachaDeliveryResolutionResult
        {
            FileReference = fileReference,
            BankReceived = bankReceived,
            PaymentStatus = status.ToString(),
            PaymentsUpdated = drafts.Count,
        };
    }

    public async Task<NachaRetrievedFile> RetrieveHeldNachaFileAsync(string fileReference, string reason)
    {
        var file = await _dispatcher.RetrieveAsync(_actor.TenantId, fileReference, new NachaActor(ActorId, _actor.IsService), reason);
        if (file.FirstRetrieval)
        {
            // The platform admin now delivers it by hand: its drafts count as submitted.
            var now = DateTime.UtcNow;
            foreach (var draft in (await _draftRepository.GetByStatusAsync(EftDraftStatus.AwaitingRetrieval))
                     .Where(d => d.NachaFileReference == fileReference))
            {
                draft.Status = EftDraftStatus.Submitted;
                draft.SubmittedAt = now;
                draft.ExpectedSettlementDate = now.AddBusinessDays(2);
                draft.ErrorMessage = $"NACHA file retrieved by platform admin {ActorId} for manual delivery to the bank.";
                draft.LastUpdatedBy = ActorId;
                await _draftRepository.UpdateAsync(draft);
            }
        }
        return file;
    }

    private static string? Last4(string? number)
        => string.IsNullOrEmpty(number) ? null : number.Length <= 4 ? number : number[^4..];

    private static NachaEntrySummary Summary(EftDraft draft) => new()
    {
        DraftId = draft.Id,
        InvoiceId = draft.InvoiceId,
        GroupNumber = draft.GroupNumber,
        RoutingNumberLast4 = draft.RoutingNumberLast4,
        AccountNumberLast4 = draft.AccountNumberLast4,
        Amount = draft.Amount,
        TraceNumber = draft.TraceNumber,
    };

    public Task<EftDraft> ProcessAchReturnAsync(ProcessAchReturnRequest request)
        => ProcessAchReturnAsync(request, ActorId);

    private async Task<EftDraft> ProcessAchReturnAsync(ProcessAchReturnRequest request, string actor)
    {
        var draft = await _draftRepository.GetByIdAsync(request.DraftId)
            ?? throw new InvalidOperationException($"Draft {request.DraftId} not found");

        if (draft.Status != EftDraftStatus.Submitted && draft.Status != EftDraftStatus.Processing)
            throw new InvalidOperationException($"Cannot process return for draft in {draft.Status} state");

        draft.Status = EftDraftStatus.Returned;
        draft.ReturnCode = request.ReturnCode;
        draft.ReturnReason = request.ReturnReason ?? MapReturnCodeToReason(request.ReturnCode);
        draft.ReturnedAt = DateTime.UtcNow;
        draft.LastUpdatedBy = actor;

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
            invoice.LastUpdatedBy = actor;

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

    public Task<EftDraft> SettleDraftAsync(string draftId) => SettleDraftAsync(draftId, ActorId);

    private async Task<EftDraft> SettleDraftAsync(string draftId, string actor)
    {
        var draft = await _draftRepository.GetByIdAsync(draftId)
            ?? throw new InvalidOperationException($"Draft {draftId} not found");

        if (draft.Status != EftDraftStatus.Submitted && draft.Status != EftDraftStatus.Processing)
            throw new InvalidOperationException($"Cannot settle draft in {draft.Status} state");

        draft.Status = EftDraftStatus.Settled;
        draft.SettledAt = DateTime.UtcNow;
        draft.LastUpdatedBy = actor;
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
                ReceivedDate = DateTime.UtcNow,
                RecordedBy = actor
            };

            invoice.Payments.Add(payment);
            invoice.RecalculateTotals();
            invoice.LastUpdatedBy = actor;

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

        // The webhook is anonymous (authenticated by the Stripe signature just
        // verified), so the tenant comes from the signed event: the tenant_id we
        // wrote into the PaymentIntent metadata. No tenant, no processing.
        if (string.IsNullOrEmpty(webhookResult.TenantId))
        {
            _logger.LogError(
                "Stripe event for PaymentIntent {PaymentIntentId} carries no tenant_id metadata; not processed and needs attention",
                webhookResult.PaymentIntentId);
            return;
        }
        var http = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("Stripe webhook processed outside a request");
        if (http.Items["TenantId"] is string existing && !string.Equals(existing, webhookResult.TenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("Stripe event tenant does not match the request tenant");
        http.Items["TenantId"] = webhookResult.TenantId;

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
                await SettleDraftAsync(draft.Id, StripeWebhookActor);
                break;

            case "payment_failed":
                await ProcessAchReturnAsync(new ProcessAchReturnRequest
                {
                    DraftId = draft.Id,
                    ReturnCode = webhookResult.FailureCode ?? "STRIPE_FAIL",
                    ReturnReason = webhookResult.FailureMessage
                }, StripeWebhookActor);
                break;

            case "payment_cancelled":
                draft.Status = EftDraftStatus.Cancelled;
                draft.LastUpdatedBy = StripeWebhookActor;
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
        draft.LastUpdatedBy = ActorId;
        return await _draftRepository.UpdateAsync(draft);
    }

    // --- Private helpers ---

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
