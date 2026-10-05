using System.Text.Json;
using CapitationService.Models;
using CapitationService.Repositories;
using CloudHealthOffice.NachaTransmission;

namespace CapitationService.Services;

/// <summary>
/// Orchestrates EFT/check disbursements for capitation statements to providers.
/// The credit-side equivalent of EftDraftService — where EftDraftService debits sponsors
/// for premiums owed, this service credits providers for capitation payments earned.
/// Coordinates between NACHA credit file generation, Stripe Connect transfers, and
/// statement lifecycle management.
/// </summary>
public interface ICapitationDisbursementService
{
    /// <summary>
    /// Initiate a disbursement for a single capitation statement
    /// </summary>
    Task<CapitationDisbursement> InitiateDisbursementAsync(InitiateDisbursementRequest request);

    /// <summary>
    /// Initiate disbursements for a batch of statements (from capitation run or statement list)
    /// </summary>
    Task<BatchDisbursementResult> InitiateBatchDisbursementAsync(InitiateBatchDisbursementRequest request);

    /// <summary>
    /// Generate a NACHA credit file for all pending NACHA disbursements and send
    /// it to the bank. <paramref name="releasedBy"/> is the user releasing the
    /// file (token subject). Returns the masked summary and receipt, never the file.
    /// </summary>
    Task<NachaCreditFileResult> GenerateNachaCreditFileAsync(string releasedBy);

    /// <summary>NACHA files of the tenant that were not delivered (no content).</summary>
    Task<IReadOnlyList<NachaHeldFile>> ListHeldNachaFilesAsync(string tenantId);

    /// <summary>Re-sends a held NACHA file; the actor must not be the user who released it.</summary>
    Task<NachaCreditFileResult> RetryNachaTransmissionAsync(string tenantId, string fileReference, NachaActor actor);

    /// <summary>A held NACHA file for a platform admin (not the releaser), audited with the reason.</summary>
    Task<NachaRetrievedFile> RetrieveHeldNachaFileAsync(string tenantId, string fileReference, NachaActor actor, string reason);

    /// <summary>
    /// Process an ACH return (bank rejection of credit)
    /// </summary>
    Task<CapitationDisbursement> ProcessReturnAsync(ProcessReturnRequest request);

    /// <summary>
    /// Mark a disbursement as settled (payment confirmed)
    /// </summary>
    Task<CapitationDisbursement> SettleDisbursementAsync(string id);

    /// <summary>
    /// Process Stripe webhook and update disbursement/statement accordingly
    /// </summary>
    Task ProcessStripeWebhookAsync(string json, string stripeSignature);

    /// <summary>
    /// Get all disbursements for a statement
    /// </summary>
    Task<IEnumerable<CapitationDisbursement>> GetDisbursementsByStatementAsync(string statementId);

    /// <summary>
    /// Get disbursement by ID
    /// </summary>
    Task<CapitationDisbursement?> GetDisbursementByIdAsync(string id);

    /// <summary>
    /// Cancel a pending disbursement
    /// </summary>
    Task<CapitationDisbursement> CancelDisbursementAsync(string id);
}

/// <summary>
/// The payment is already being released by another request (409): a statement
/// is paid once, and a disbursement is in at most one NACHA file.
/// </summary>
public sealed class PaymentReleaseConflictException : Exception
{
    public PaymentReleaseConflictException(string message) : base(message) { }
}

public class CapitationDisbursementService : ICapitationDisbursementService
{
    private readonly ICapitationDisbursementRepository _disbursementRepository;
    private readonly ICapitationStatementRepository _statementRepository;
    private readonly ICapitationRunRepository _runRepository;
    private readonly INachaCreditFileService _nachaCreditFileService;
    private readonly IStripeConnectService _stripeConnectService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IPaymentSeparationOfDuties _separationOfDuties;
    private readonly IProviderBankAccountSource _bankAccounts;
    private readonly INachaDispatcher _dispatcher;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger<CapitationDisbursementService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CapitationDisbursementService(
        ICapitationDisbursementRepository disbursementRepository,
        ICapitationStatementRepository statementRepository,
        ICapitationRunRepository runRepository,
        INachaCreditFileService nachaCreditFileService,
        IStripeConnectService stripeConnectService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IPaymentSeparationOfDuties separationOfDuties,
        IProviderBankAccountSource bankAccounts,
        INachaDispatcher dispatcher,
        ILogger<CapitationDisbursementService> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _separationOfDuties = separationOfDuties;
        _bankAccounts = bankAccounts;
        _dispatcher = dispatcher;
        _disbursementRepository = disbursementRepository;
        _statementRepository = statementRepository;
        _runRepository = runRepository;
        _nachaCreditFileService = nachaCreditFileService;
        _stripeConnectService = stripeConnectService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CapitationDisbursement> InitiateDisbursementAsync(InitiateDisbursementRequest request)
    {
        _separationOfDuties.EnsureUserToken(PaymentAction.Release);

        var statement = await _statementRepository.GetByIdAsync(request.StatementId)
            ?? throw new InvalidOperationException($"Statement {request.StatementId} not found");

        if (statement.Status != CapitationStatementStatus.Approved)
            throw new InvalidOperationException($"Cannot disburse against {statement.Status} statement — must be Approved");

        if (statement.NetPayable <= 0)
            throw new InvalidOperationException("Statement has no net payable amount");

        // Maker-checker: whoever prepared the statement cannot release its payment.
        await _separationOfDuties.EnsureActorIsNotMakerAsync(statement, request.InitiatedBy, PaymentAction.Release);

        // The provider's approved account, masked (method, Stripe id, last 4).
        // Full numbers are read only when a NACHA file is built.
        var (bankAccount, problem) = await FetchProviderBankAccountAsync(statement.TenantId, statement.ProviderNPI);
        if (bankAccount == null)
            throw new InvalidOperationException(problem);
        if (!bankAccount.EftEnabled)
            throw new InvalidOperationException($"EFT not enabled for provider {statement.ProviderNPI}");

        // An override may pay part of the approved amount, never more than it.
        if (request.Amount is { } requested && (requested <= 0 || requested > statement.NetPayable))
            throw new InvalidOperationException(
                $"Disbursement amount must be greater than 0 and at most the approved net payable {statement.NetPayable:N2}");

        var amount = request.Amount ?? statement.NetPayable;
        var method = request.Method ?? MapPreferredMethod(bankAccount.PreferredDisbursementMethod);

        // Validate bank account for chosen method
        ValidateBankAccountForMethod(bankAccount, method, statement.ProviderNPI);

        var disbursement = new CapitationDisbursement
        {
            StatementId = statement.Id,
            StatementNumber = statement.StatementNumber,
            ProviderNPI = statement.ProviderNPI,
            ProviderName = statement.ProviderName,
            Amount = amount,
            Method = method,
            Status = DisbursementStatus.Pending,
            RoutingNumberLast4 = bankAccount.RoutingNumberLast4,
            AccountNumberLast4 = bankAccount.AccountNumberLast4,
            InitiatedBy = request.InitiatedBy
        };

        // Approved to PaymentInitiated, as one conditional write, before any money
        // moves: of two releases of the same statement only one gets past here.
        if (!await _statementRepository.TryStartPaymentAsync(statement.Id, disbursement.Id))
            throw new PaymentReleaseConflictException(
                $"Statement {statement.StatementNumber} is already being paid by another release; nothing was sent.");
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        statement.EftDisbursementId = disbursement.Id;

        var transferCreated = false;
        try
        {
            // For Stripe Connect, initiate transfer immediately
            if (method == DisbursementMethod.StripeConnect)
            {
                var result = await _stripeConnectService.CreateTransferAsync(
                    bankAccount.StripeConnectedAccountId!,
                    amount,
                    statement.StatementNumber,
                    statement.ProviderNPI,
                    statement.TenantId);

                if (result.Status == "failed")
                {
                    disbursement.Status = DisbursementStatus.Failed;
                    disbursement.ErrorMessage = result.ErrorMessage;
                }
                else
                {
                    transferCreated = true;
                    disbursement.StripeTransferId = result.TransferId;
                    disbursement.Status = DisbursementStatus.Submitted;
                    disbursement.SubmittedAt = DateTime.UtcNow;
                    disbursement.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
                }
            }
            // For NACHA, disbursement stays Pending until a NACHA credit file is generated
            else if (method == DisbursementMethod.NachaCredit)
            {
                disbursement.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
            }
            // For Check, just mark as submitted (manual fulfillment)
            else
            {
                disbursement.Status = DisbursementStatus.Submitted;
                disbursement.SubmittedAt = DateTime.UtcNow;
            }

            disbursement = await _disbursementRepository.CreateAsync(disbursement);
        }
        catch (Exception ex)
        {
            if (transferCreated)
            {
                // The money went to Stripe: the statement must not become payable again.
                _logger.LogCritical(ex,
                    "Stripe transfer {TransferId} for statement {StatementNumber} was created but disbursement {DisbursementId} " +
                    "could not be recorded; the statement stays PaymentInitiated and needs reconciliation",
                    disbursement.StripeTransferId, statement.StatementNumber, disbursement.Id);
            }
            else
            {
                await _statementRepository.UndoStartPaymentAsync(statement.Id, disbursement.Id);
            }
            throw;
        }

        _logger.LogInformation(
            "Initiated {Method} disbursement {DisbursementId} for statement {StatementNumber}, amount ${Amount:N2}",
            method, disbursement.Id, statement.StatementNumber, amount);

        return disbursement;
    }

    public async Task<BatchDisbursementResult> InitiateBatchDisbursementAsync(InitiateBatchDisbursementRequest request)
    {
        _separationOfDuties.EnsureUserToken(PaymentAction.Release);

        var result = new BatchDisbursementResult();
        var statementIds = new List<string>(request.StatementIds);

        // If capitation run specified, get all statement IDs from it
        if (!string.IsNullOrEmpty(request.CapitationRunId))
        {
            var run = await _runRepository.GetByIdAsync(request.CapitationRunId)
                ?? throw new InvalidOperationException($"Capitation run {request.CapitationRunId} not found");
            statementIds.AddRange(run.StatementIds);
        }

        var uniqueStatementIds = statementIds.Distinct().ToList();
        result.TotalStatements = uniqueStatementIds.Count;

        // Maker-checker before any money moves: if the releasing user prepared any
        // statement this batch would pay, the whole batch is refused.
        foreach (var statementId in uniqueStatementIds)
        {
            var candidate = await _statementRepository.GetByIdAsync(statementId);
            if (candidate is { Status: CapitationStatementStatus.Approved } && candidate.NetPayable > 0)
                await _separationOfDuties.EnsureActorIsNotMakerAsync(candidate, request.InitiatedBy, PaymentAction.Release);
        }

        var nachaEntries = new List<NachaCreditEntryDetail>();
        var nachaDisbursements = new List<CapitationDisbursement>();
        // The NACHA disbursements this batch creates are born claimed by it
        // (Releasing), so a concurrent NACHA release never puts them in a second file.
        var claimId = NewClaimId();
        var claimedAt = DateTime.UtcNow;

        foreach (var statementId in uniqueStatementIds)
        {
            try
            {
                var statement = await _statementRepository.GetByIdAsync(statementId);
                if (statement == null || statement.NetPayable <= 0 ||
                    statement.Status != CapitationStatementStatus.Approved)
                {
                    result.Skipped++;
                    continue;
                }

                var (bankAccount, problem) = await FetchProviderBankAccountAsync(statement.TenantId, statement.ProviderNPI);
                if (bankAccount == null)
                {
                    // No approved account (or provider-service did not answer): never a silent skip.
                    NeedsAttention(result, statement.Id, null, statement.ProviderNPI, problem!);
                    continue;
                }
                if (!bankAccount.EftEnabled)
                {
                    result.Skipped++;
                    continue;
                }

                var method = request.Method ?? MapPreferredMethod(bankAccount.PreferredDisbursementMethod);

                if (method == DisbursementMethod.StripeConnect)
                {
                    // Initiate individually via Stripe
                    var disbursement = await InitiateDisbursementAsync(new InitiateDisbursementRequest
                    {
                        StatementId = statementId,
                        Method = DisbursementMethod.StripeConnect,
                        InitiatedBy = request.InitiatedBy
                    });
                    result.DisbursementIds.Add(disbursement.Id);
                    result.DisbursementsInitiated++;
                    result.TotalAmount += disbursement.Amount;
                }
                else if (method == DisbursementMethod.NachaCredit)
                {
                    // The releasing user passed payments:approve and separation of duties
                    // (above) for every statement in the batch: only now are the full
                    // numbers read, with capitation-service's own token.
                    var full = await _bankAccounts.GetForDisbursementAsync(statement.TenantId, statement.ProviderNPI);
                    if (!full.Found)
                    {
                        NeedsAttention(result, statement.Id, null, statement.ProviderNPI, full.Reason!);
                        continue;
                    }
                    var payee = full.Account!;

                    // Collect for NACHA batch
                    var disbursement = new CapitationDisbursement
                    {
                        StatementId = statement.Id,
                        StatementNumber = statement.StatementNumber,
                        ProviderNPI = statement.ProviderNPI,
                        ProviderName = statement.ProviderName,
                        Amount = statement.NetPayable,
                        Method = DisbursementMethod.NachaCredit,
                        Status = DisbursementStatus.Releasing,
                        ReleaseClaimId = claimId,
                        ReleaseClaimedAt = claimedAt,
                        RoutingNumberLast4 = payee.RoutingNumberLast4 ?? bankAccount.RoutingNumberLast4,
                        AccountNumberLast4 = payee.AccountNumberLast4 ?? bankAccount.AccountNumberLast4,
                        InitiatedBy = request.InitiatedBy
                    };

                    // Approved to PaymentInitiated as one conditional write: a statement
                    // another release is already paying is left out of this file.
                    if (!await _statementRepository.TryStartPaymentAsync(statement.Id, disbursement.Id))
                    {
                        result.Errors++;
                        result.ErrorMessages.Add($"Statement {statementId}: already being paid by another release; not included.");
                        continue;
                    }
                    try
                    {
                        disbursement = await _disbursementRepository.CreateAsync(disbursement);
                    }
                    catch
                    {
                        await _statementRepository.UndoStartPaymentAsync(statement.Id, disbursement.Id);
                        throw;
                    }
                    nachaDisbursements.Add(disbursement);

                    nachaEntries.Add(new NachaCreditEntryDetail
                    {
                        RoutingNumber = payee.RoutingNumber!,
                        AccountNumber = payee.AccountNumber!,
                        AccountType = MapAccountType(payee.AccountType),
                        Amount = statement.NetPayable,
                        ProviderNpi = statement.ProviderNPI,
                        IndividualName = payee.AccountHolderName ?? statement.ProviderName,
                        IndividualId = statement.ProviderNPI
                    });

                    statement.Status = CapitationStatementStatus.PaymentInitiated;
                    statement.EftDisbursementId = disbursement.Id;

                    result.DisbursementIds.Add(disbursement.Id);
                    result.DisbursementsInitiated++;
                    result.TotalAmount += statement.NetPayable;
                }
                else
                {
                    // Check — initiate individually
                    var disbursement = await InitiateDisbursementAsync(new InitiateDisbursementRequest
                    {
                        StatementId = statementId,
                        Method = DisbursementMethod.Check,
                        InitiatedBy = request.InitiatedBy
                    });
                    result.DisbursementIds.Add(disbursement.Id);
                    result.DisbursementsInitiated++;
                    result.TotalAmount += disbursement.Amount;
                }
            }
            catch (Exception ex) when (ex is not SeparationOfDutiesException)
            {
                result.Errors++;
                result.ErrorMessages.Add($"Statement {statementId}: {ex.Message}");
                _logger.LogWarning(ex, "Failed to initiate disbursement for statement {StatementId}", statementId);
            }
        }

        // Build the NACHA credit file and send it straight to the bank. The
        // disbursements are Submitted only once the bank has it; otherwise they
        // await retrieval (file held encrypted) or stay Pending (nothing held).
        if (nachaEntries.Count > 0)
        {
            result.NachaFile = await SendNachaFileAsync(
                nachaEntries, nachaDisbursements, request.InitiatedBy ?? string.Empty, request.CapitationRunId, claimId);
        }

        _logger.LogInformation(
            "Batch disbursement: {Initiated}/{Total} disbursements initiated, {Skipped} skipped, {Errors} errors " +
            "({NeedsAttention} need attention), ${Amount:N2} total",
            result.DisbursementsInitiated, result.TotalStatements, result.Skipped, result.Errors, result.NeedsAttention.Count,
            result.TotalAmount);

        return result;
    }

    public async Task<NachaCreditFileResult> GenerateNachaCreditFileAsync(string releasedBy)
    {
        _separationOfDuties.EnsureUserToken(PaymentAction.Release);

        var pendingDisbursements = (await _disbursementRepository.GetByStatusAsync(DisbursementStatus.Pending))
            .Where(d => d.Method == DisbursementMethod.NachaCredit)
            .ToList();

        if (pendingDisbursements.Count == 0)
            throw new InvalidOperationException("No pending NACHA credit disbursements to process");

        // Generating the file is what sends the money. Maker-checker before any of it:
        // if the releasing user prepared any statement in the file, no file is generated.
        var checkedStatements = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statementId in pendingDisbursements.Select(d => d.StatementId).Distinct())
        {
            var statement = await _statementRepository.GetByIdAsync(statementId);
            if (statement != null)
            {
                await _separationOfDuties.EnsureActorIsNotMakerAsync(statement, releasedBy, PaymentAction.Release);
                checkedStatements.Add(statementId);
            }
            else
                _logger.LogWarning("Separation of duties not checked for statement {StatementId}: statement not found", statementId);
        }

        // Claim the disbursements before anything is built: Pending to Releasing,
        // one conditional write each. A concurrent release gets none of these, so
        // the same credit can never be in two files at the bank.
        var claimId = NewClaimId();
        var claimedAt = DateTime.UtcNow;
        var claimed = new List<CapitationDisbursement>();
        foreach (var disbursement in pendingDisbursements)
        {
            if (!await _disbursementRepository.TryClaimForReleaseAsync(disbursement.Id, claimId, claimedAt))
                continue;
            disbursement.Status = DisbursementStatus.Releasing;
            disbursement.ReleaseClaimId = claimId;
            disbursement.ReleaseClaimedAt = claimedAt;
            claimed.Add(disbursement);
        }
        if (claimed.Count == 0)
            throw new PaymentReleaseConflictException(
                "The pending NACHA credit disbursements are already being released by another request; nothing was sent.");

        var entries = new List<NachaCreditEntryDetail>();
        var includedDisbursements = new List<CapitationDisbursement>();
        var needsAttention = new List<DisbursementAttentionItem>();

        // Only now, with the release checks passed, are the full numbers read
        // (capitation-service's own token, never the releasing user's).
        try
        {
            foreach (var disbursement in claimed)
            {
                string? reason = null;
                ProviderBankAccountDto? payee = null;
                if (!checkedStatements.Contains(disbursement.StatementId))
                {
                    reason = $"Statement {disbursement.StatementId} was not found, so separation of duties cannot be checked. Needs attention.";
                }
                else
                {
                    var lookup = await _bankAccounts.GetForDisbursementAsync(disbursement.TenantId, disbursement.ProviderNPI);
                    if (lookup.Found) payee = lookup.Account;
                    else reason = lookup.Reason;
                }

                if (payee == null)
                {
                    reason ??= "Provider bank details unavailable. Needs attention.";
                    needsAttention.Add(new DisbursementAttentionItem
                    {
                        DisbursementId = disbursement.Id,
                        StatementId = disbursement.StatementId,
                        ProviderNPI = disbursement.ProviderNPI,
                        Reason = reason
                    });
                    // Back to Pending, with the reason on the record.
                    await ReleaseClaimsAsync(new[] { disbursement }, claimId, reason);
                    _logger.LogWarning("Disbursement {DisbursementId} for provider {NPI} left out of the NACHA file: needs attention",
                        SanitizeForLog(disbursement.Id), SanitizeForLog(disbursement.ProviderNPI));
                    continue;
                }

                // The masked summary shows the last 4 of the account actually paid.
                disbursement.RoutingNumberLast4 = payee.RoutingNumberLast4 ?? Last4(payee.RoutingNumber) ?? disbursement.RoutingNumberLast4;
                disbursement.AccountNumberLast4 = payee.AccountNumberLast4 ?? Last4(payee.AccountNumber) ?? disbursement.AccountNumberLast4;
                entries.Add(new NachaCreditEntryDetail
                {
                    RoutingNumber = payee.RoutingNumber!,
                    AccountNumber = payee.AccountNumber!,
                    AccountType = MapAccountType(payee.AccountType),
                    Amount = disbursement.Amount,
                    ProviderNpi = disbursement.ProviderNPI,
                    IndividualName = payee.AccountHolderName ?? disbursement.ProviderName,
                    IndividualId = disbursement.ProviderNPI
                });
                includedDisbursements.Add(disbursement);
            }
        }
        catch
        {
            // Nothing was built or sent: every disbursement this release still holds goes back.
            await ReleaseClaimsAsync(claimed.Where(d => d.Status == DisbursementStatus.Releasing), claimId,
                "The NACHA release failed before a file was built; back to Pending for the next release.");
            throw;
        }

        if (entries.Count == 0)
            throw new InvalidOperationException(
                "No disbursements with approved bank accounts to include in NACHA credit file. Needs attention: " +
                string.Join(" | ", needsAttention.Select(a => $"{a.DisbursementId}: {a.Reason}")));

        var result = await SendNachaFileAsync(entries, includedDisbursements, releasedBy, runId: null, claimId);
        result.NeedsAttention = needsAttention;
        return result;
    }

    /// <summary>
    /// Generates the credit file, hands it to the dispatcher (bank SFTP, or
    /// held encrypted for retrieval), updates the disbursements to match what
    /// happened, and returns the masked summary. The file content never leaves
    /// this method.
    /// </summary>
    private static string NewClaimId() => Guid.NewGuid().ToString("N");

    /// <summary>Releasing (under this claim) back to Pending: nothing of theirs was sent.</summary>
    private async Task ReleaseClaimsAsync(IEnumerable<CapitationDisbursement> disbursements, string claimId, string reason)
    {
        foreach (var disbursement in disbursements.ToList())
        {
            try
            {
                await _disbursementRepository.ReleaseClaimAsync(disbursement.Id, claimId, reason);
                disbursement.Status = DisbursementStatus.Pending;
                disbursement.ReleaseClaimId = null;
                disbursement.ReleaseClaimedAt = null;
                disbursement.ErrorMessage = reason;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Disbursement {DisbursementId} could not be released from NACHA release {ClaimId}; it stays Releasing and needs attention",
                    SanitizeForLog(disbursement.Id), claimId);
            }
        }
    }

    private async Task<NachaCreditFileResult> SendNachaFileAsync(
        List<NachaCreditEntryDetail> entries, List<CapitationDisbursement> disbursements, string releasedBy, string? runId, string claimId)
    {
        // Repositories stamp the token tenant on every disbursement they write or read.
        var tenants = disbursements.Select(d => d.TenantId).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
        if (tenants.Count > 1)
            throw new InvalidOperationException("A NACHA credit file must hold disbursements of exactly one tenant.");
        var tenantId = tenants.SingleOrDefault() ?? string.Empty;

        GeneratedNachaCreditFile file;
        NachaFileFacts facts;
        try
        {
            file = _nachaCreditFileService.GenerateNachaCreditFile(entries, BuildNachaCreditOptionsFromConfig());
            facts = NachaFileFacts.From(file.FileContent);
        }
        catch
        {
            await ReleaseClaimsAsync(disbursements, claimId,
                "The NACHA credit file could not be built; nothing was sent. Back to Pending for the next release.");
            throw;
        }

        NachaDispatchOutcome outcome;
        try
        {
            outcome = await _dispatcher.DispatchAsync(new NachaTransmissionRequest
            {
                TenantId = tenantId,
                FileReference = file.FileReference,
                FileName = file.FileName,
                Content = file.FileContent,
                RunId = runId,
                BatchId = file.FileReference,
                TransmittedBy = releasedBy,
            });
        }
        catch (Exception ex)
        {
            // The dispatcher answers every transmission failure with an outcome; an
            // exception here means it is not known whether the bank got the file.
            // The disbursements stay Releasing (never back to Pending, which could
            // pay them again) until someone checks with the bank.
            _logger.LogCritical(ex,
                "NACHA credit file {FileReference} (release {ClaimId}): delivery to the bank is unknown; its {Count} disbursements " +
                "stay Releasing and must be checked with the bank before anything is re-sent",
                file.FileReference, claimId, disbursements.Count);
            throw;
        }
        finally
        {
            file.FileContent = string.Empty;
        }

        var now = DateTime.UtcNow;
        var result = new NachaCreditFileResult
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

        for (int i = 0; i < disbursements.Count; i++)
        {
            var disbursement = disbursements[i];
            switch (outcome.Status)
            {
                case NachaTransmissionStatus.Transmitted:
                    disbursement.NachaFileReference = file.FileReference;
                    disbursement.TraceNumber = entries[i].TraceNumber;
                    disbursement.Status = DisbursementStatus.Submitted;
                    disbursement.SubmittedAt = now;
                    disbursement.ExpectedSettlementDate = now.AddBusinessDays(2);
                    disbursement.ErrorMessage = null;
                    break;
                case NachaTransmissionStatus.AwaitingRetrieval:
                    disbursement.NachaFileReference = file.FileReference;
                    disbursement.TraceNumber = entries[i].TraceNumber;
                    disbursement.Status = DisbursementStatus.AwaitingRetrieval;
                    disbursement.ErrorMessage =
                        $"NACHA file {file.FileReference} was not delivered to the bank: {outcome.Reason} It is held encrypted for 7 days: " +
                        "a platform admin must retrieve it, or another user with payments:approve must retry it.";
                    break;
                default:
                    // Nothing was sent or held: back to Pending for the next file.
                    disbursement.NachaFileReference = null;
                    disbursement.TraceNumber = null;
                    disbursement.Status = DisbursementStatus.Pending;
                    disbursement.ReleaseClaimId = null;
                    disbursement.ReleaseClaimedAt = null;
                    disbursement.ErrorMessage = outcome.Reason;
                    break;
            }
            await _disbursementRepository.UpdateAsync(disbursement);

            var summary = Summary(disbursement);
            summary.ProviderName = entries[i].IndividualName;
            result.Entries.Add(summary);
        }

        return result;
    }

    public Task<IReadOnlyList<NachaHeldFile>> ListHeldNachaFilesAsync(string tenantId)
        => _dispatcher.ListHeldAsync(tenantId);

    public async Task<NachaCreditFileResult> RetryNachaTransmissionAsync(string tenantId, string fileReference, NachaActor actor)
    {
        var disbursements = (await _disbursementRepository.GetByStatusAsync(DisbursementStatus.AwaitingRetrieval))
            .Where(d => d.NachaFileReference == fileReference)
            .ToList();

        NachaDispatchOutcome outcome;
        try
        {
            outcome = await _dispatcher.RetryAsync(tenantId, fileReference, actor);
        }
        catch (Exception ex) when (ex is NachaHeldFileExpiredException or NachaHeldFileNotFoundException)
        {
            // The held file is gone: back to Pending, so the next release builds a new file.
            foreach (var disbursement in disbursements)
            {
                disbursement.Status = DisbursementStatus.Pending;
                disbursement.NachaFileReference = null;
                disbursement.TraceNumber = null;
                disbursement.ErrorMessage = $"Held NACHA file {fileReference} expired before it was delivered; back to Pending for the next file.";
                await _disbursementRepository.UpdateAsync(disbursement);
            }
            throw;
        }

        var held = await _dispatcher.GetHeldAsync(tenantId, fileReference);
        var result = new NachaCreditFileResult
        {
            FileReference = fileReference,
            FileName = held?.FileName ?? string.Empty,
            EntryCount = held?.EntryCount ?? disbursements.Count,
            TotalAmount = disbursements.Sum(d => d.Amount),
            TotalDebitAmount = held?.TotalDebitAmount ?? 0,
            TotalCreditAmount = held?.TotalCreditAmount ?? 0,
            GeneratedAt = held?.CreatedAt ?? DateTime.UtcNow,
            TransmissionStatus = outcome.Status.ToString(),
            TransmissionError = outcome.Reason,
            HeldUntil = outcome.HeldUntil,
            Receipt = outcome.Receipt,
        };

        var now = DateTime.UtcNow;
        foreach (var disbursement in disbursements)
        {
            if (outcome.Status == NachaTransmissionStatus.Transmitted)
            {
                disbursement.Status = DisbursementStatus.Submitted;
                disbursement.SubmittedAt = now;
                disbursement.ExpectedSettlementDate = now.AddBusinessDays(2);
                disbursement.ErrorMessage = null;
                await _disbursementRepository.UpdateAsync(disbursement);
            }
            result.Entries.Add(Summary(disbursement));
        }

        return result;
    }

    public async Task<NachaRetrievedFile> RetrieveHeldNachaFileAsync(string tenantId, string fileReference, NachaActor actor, string reason)
    {
        var file = await _dispatcher.RetrieveAsync(tenantId, fileReference, actor, reason);
        if (file.FirstRetrieval)
        {
            // The platform admin now delivers it by hand: its disbursements count as submitted.
            var now = DateTime.UtcNow;
            foreach (var disbursement in (await _disbursementRepository.GetByStatusAsync(DisbursementStatus.AwaitingRetrieval))
                     .Where(d => d.NachaFileReference == fileReference))
            {
                disbursement.Status = DisbursementStatus.Submitted;
                disbursement.SubmittedAt = now;
                disbursement.ExpectedSettlementDate = now.AddBusinessDays(2);
                disbursement.ErrorMessage = $"NACHA file retrieved by platform admin {actor.UserId} for manual delivery to the bank.";
                await _disbursementRepository.UpdateAsync(disbursement);
            }
        }
        return file;
    }

    private static string? Last4(string? number)
        => string.IsNullOrEmpty(number) ? null : number.Length <= 4 ? number : number[^4..];

    private static NachaCreditEntrySummary Summary(CapitationDisbursement d) => new()
    {
        DisbursementId = d.Id,
        StatementId = d.StatementId,
        ProviderNPI = d.ProviderNPI,
        ProviderName = d.ProviderName,
        RoutingNumberLast4 = d.RoutingNumberLast4,
        AccountNumberLast4 = d.AccountNumberLast4,
        Amount = d.Amount,
        TraceNumber = d.TraceNumber,
    };

    public async Task<CapitationDisbursement> ProcessReturnAsync(ProcessReturnRequest request)
    {
        var disbursement = await _disbursementRepository.GetByIdAsync(request.DisbursementId)
            ?? throw new InvalidOperationException($"Disbursement {request.DisbursementId} not found");

        if (disbursement.Status != DisbursementStatus.Submitted && disbursement.Status != DisbursementStatus.Processing)
            throw new InvalidOperationException($"Cannot process return for disbursement in {disbursement.Status} state");

        disbursement.Status = DisbursementStatus.Returned;
        disbursement.ReturnCode = request.ReturnCode;
        disbursement.ReturnReason = request.ReturnReason ?? MapReturnCodeToReason(request.ReturnCode);
        disbursement.ReturnedAt = DateTime.UtcNow;

        await _disbursementRepository.UpdateAsync(disbursement);

        // Revert the statement back to Approved so it can be re-disbursed
        var statement = await _statementRepository.GetByIdAsync(disbursement.StatementId);
        if (statement != null)
        {
            statement.Status = CapitationStatementStatus.Approved;
            statement.EftDisbursementId = null;
            statement.PaymentDate = null;

            statement.Adjustments.Add(new CapitationAdjustment
            {
                Type = CapitationAdjustmentType.Other,
                Description = $"ACH return ({disbursement.ReturnCode}): {disbursement.ReturnReason}",
                Amount = 0,
                AdjustmentDate = DateTime.UtcNow
            });

            await _statementRepository.UpdateAsync(statement);
        }

        _logger.LogWarning(
            "ACH return processed for disbursement {DisbursementId}, statement {StatementNumber}: {ReturnCode} - {ReturnReason}",
            disbursement.Id, disbursement.StatementNumber,
            SanitizeForLog(disbursement.ReturnCode), SanitizeForLog(disbursement.ReturnReason));

        // Check if auto-retry is appropriate
        if (ShouldRetry(disbursement))
        {
            _logger.LogInformation("Auto-retry eligible for disbursement {DisbursementId} (attempt {RetryCount}/{MaxRetries})",
                disbursement.Id, disbursement.RetryCount + 1, disbursement.MaxRetries);
        }

        return disbursement;
    }

    public async Task<CapitationDisbursement> SettleDisbursementAsync(string id)
    {
        var disbursement = await _disbursementRepository.GetByIdAsync(id)
            ?? throw new InvalidOperationException($"Disbursement {id} not found");

        if (disbursement.Status != DisbursementStatus.Submitted && disbursement.Status != DisbursementStatus.Processing)
            throw new InvalidOperationException($"Cannot settle disbursement in {disbursement.Status} state");

        disbursement.Status = DisbursementStatus.Settled;
        disbursement.SettledAt = DateTime.UtcNow;
        await _disbursementRepository.UpdateAsync(disbursement);

        // Update the statement to Paid
        var statement = await _statementRepository.GetByIdAsync(disbursement.StatementId);
        if (statement != null)
        {
            statement.Status = CapitationStatementStatus.Paid;
            statement.PaymentDate = DateTime.UtcNow;
            statement.EftDisbursementId = disbursement.Id;

            if (disbursement.Method == DisbursementMethod.Check)
                statement.CheckNumber = disbursement.CheckNumber;

            await _statementRepository.UpdateAsync(statement);
        }

        _logger.LogInformation("Disbursement {DisbursementId} settled for statement {StatementNumber}, amount ${Amount:N2}",
            disbursement.Id, disbursement.StatementNumber, disbursement.Amount);

        return disbursement;
    }

    public async Task ProcessStripeWebhookAsync(string json, string stripeSignature)
    {
        var webhookResult = await _stripeConnectService.ProcessWebhookAsync(json, stripeSignature);

        if (!webhookResult.Handled || string.IsNullOrEmpty(webhookResult.TransferId))
            return;

        // The webhook is anonymous (authenticated by the Stripe signature just
        // verified), so the tenant comes from the signed event: the tenant_id we
        // wrote into the transfer's metadata. An event without one (a payout, or a
        // transfer created before transfers carried it) cannot be matched to a
        // tenant: it is acknowledged, so Stripe stops retrying, and logged for
        // reconciliation.
        if (string.IsNullOrEmpty(webhookResult.TenantId))
        {
            _logger.LogWarning(
                "Stripe {EventType} event for {StripeObjectId} carries no tenant_id metadata; acknowledged but not processed. " +
                "Reconcile the disbursement by hand",
                SanitizeForLog(webhookResult.EventType), SanitizeForLog(webhookResult.TransferId));
            return;
        }
        var http = _httpContextAccessor?.HttpContext
            ?? throw new InvalidOperationException("Stripe webhook processed outside a request");
        if (http.Items["TenantId"] is string existing && !string.Equals(existing, webhookResult.TenantId, StringComparison.Ordinal))
            throw new InvalidOperationException("Stripe event tenant does not match the request tenant");
        http.Items["TenantId"] = webhookResult.TenantId;

        // Find the disbursement by Stripe Transfer ID
        var disbursements = await _disbursementRepository.GetByStripeTransferIdAsync(webhookResult.TransferId);
        var disbursement = disbursements.FirstOrDefault();

        if (disbursement == null)
        {
            _logger.LogWarning("No disbursement found for Transfer {TransferId}", webhookResult.TransferId);
            return;
        }

        switch (webhookResult.EventType)
        {
            case "transfer_created":
                // Transfer created — already in Submitted state, no action needed
                break;

            case "payout_paid":
                await SettleDisbursementAsync(disbursement.Id);
                break;

            case "payout_failed":
            case "transfer_reversed":
                await ProcessReturnAsync(new ProcessReturnRequest
                {
                    DisbursementId = disbursement.Id,
                    ReturnCode = webhookResult.FailureCode ?? "STRIPE_FAIL",
                    ReturnReason = webhookResult.FailureMessage
                });
                break;
        }
    }

    public async Task<IEnumerable<CapitationDisbursement>> GetDisbursementsByStatementAsync(string statementId)
    {
        return await _disbursementRepository.GetByStatementIdAsync(statementId);
    }

    public async Task<CapitationDisbursement?> GetDisbursementByIdAsync(string id)
    {
        return await _disbursementRepository.GetByIdAsync(id);
    }

    public async Task<CapitationDisbursement> CancelDisbursementAsync(string id)
    {
        var disbursement = await _disbursementRepository.GetByIdAsync(id)
            ?? throw new InvalidOperationException($"Disbursement {id} not found");

        if (disbursement.Status != DisbursementStatus.Pending)
            throw new InvalidOperationException($"Can only cancel Pending disbursements, current: {disbursement.Status}");

        // If Stripe, reverse the transfer
        if (disbursement.Method == DisbursementMethod.StripeConnect && !string.IsNullOrEmpty(disbursement.StripeTransferId))
        {
            await _stripeConnectService.CancelTransferAsync(disbursement.StripeTransferId);
        }

        disbursement.Status = DisbursementStatus.Cancelled;
        disbursement = await _disbursementRepository.UpdateAsync(disbursement);

        // Revert statement back to Approved
        var statement = await _statementRepository.GetByIdAsync(disbursement.StatementId);
        if (statement != null && statement.Status == CapitationStatementStatus.PaymentInitiated)
        {
            statement.Status = CapitationStatementStatus.Approved;
            statement.EftDisbursementId = null;
            await _statementRepository.UpdateAsync(statement);
        }

        return disbursement;
    }

    // --- Private helpers ---

    /// <summary>
    /// The provider's approved account, masked (no full numbers): EFT
    /// enrollment, method, Stripe id and last 4. Null with the reason when
    /// there is none or provider-service did not answer (needs attention).
    /// </summary>
    private async Task<(ProviderBankAccountDto? Account, string? Problem)> FetchProviderBankAccountAsync(string tenantId, string providerNpi)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ProviderService");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/providers/npi/{Uri.EscapeDataString(providerNpi)}/bank-account");
            // Names the statement's tenant for ChoOutboundTokenHandler: a
            // disbursement with no inbound caller still carries a service token
            // for it (provider-service requires one).
            request.Headers.Add("X-Tenant-ID", tenantId);
            using var response = await client.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (null, $"Provider {providerNpi} has no approved bank account in provider-service " +
                              "(a pending change must be approved by a user with payments:approve). Needs attention.");
            if (!response.IsSuccessStatusCode)
                return (null, $"provider-service answered {(int)response.StatusCode} {response.StatusCode} to the bank-account " +
                              $"read for provider {providerNpi}. Needs attention.");

            var account = await response.Content.ReadFromJsonAsync<ProviderBankAccountDto>(JsonOptions);
            return account == null
                ? (null, $"provider-service returned no bank account for provider {providerNpi}. Needs attention.")
                : (account, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to fetch bank account for provider {NPI}: {Error}", SanitizeForLog(providerNpi), ex.GetType().Name);
            return (null, $"The bank-account read for provider {providerNpi} from provider-service failed ({ex.GetType().Name}). Needs attention.");
        }
    }

    private void NeedsAttention(BatchDisbursementResult result, string? statementId, string? disbursementId, string providerNpi, string reason)
    {
        result.Errors++;
        result.ErrorMessages.Add($"Statement {statementId}: {reason}");
        result.NeedsAttention.Add(new DisbursementAttentionItem
        {
            StatementId = statementId,
            DisbursementId = disbursementId,
            ProviderNPI = providerNpi,
            Reason = reason
        });
        _logger.LogWarning("Statement {StatementId} for provider {NPI} was not disbursed: needs attention",
            SanitizeForLog(statementId), SanitizeForLog(providerNpi));
    }

    private static void ValidateBankAccountForMethod(ProviderBankAccountDto bankAccount, DisbursementMethod method, string providerNpi)
    {
        if (method == DisbursementMethod.NachaCredit)
        {
            // The masked read shows whether an account is on file (last 4); the
            // full numbers are read when the NACHA file is generated.
            var hasRouting = !string.IsNullOrEmpty(bankAccount.RoutingNumberLast4) || !string.IsNullOrEmpty(bankAccount.RoutingNumber);
            var hasAccount = !string.IsNullOrEmpty(bankAccount.AccountNumberLast4) || !string.IsNullOrEmpty(bankAccount.AccountNumber);
            if (!hasRouting || !hasAccount)
                throw new InvalidOperationException(
                    $"NACHA credit requires routing and account numbers for provider {providerNpi}");
        }
        else if (method == DisbursementMethod.StripeConnect)
        {
            if (string.IsNullOrEmpty(bankAccount.StripeConnectedAccountId))
                throw new InvalidOperationException(
                    $"Stripe Connect requires a connected account ID for provider {providerNpi}");
        }
    }

    private static DisbursementMethod MapPreferredMethod(string? preferredMethod)
    {
        return preferredMethod?.ToLowerInvariant() switch
        {
            "nachacredit" => DisbursementMethod.NachaCredit,
            "stripeconnect" => DisbursementMethod.StripeConnect,
            "check" => DisbursementMethod.Check,
            _ => DisbursementMethod.NachaCredit
        };
    }

    private static BankAccountType MapAccountType(string? accountType)
    {
        return accountType?.ToLowerInvariant() switch
        {
            "savings" => BankAccountType.Savings,
            _ => BankAccountType.Checking
        };
    }

    private NachaCreditFileOptions BuildNachaCreditOptionsFromConfig()
    {
        return new NachaCreditFileOptions
        {
            ImmediateDestination = _configuration["Nacha:ImmediateDestination"] ?? "",
            ImmediateOrigin = _configuration["Nacha:ImmediateOrigin"] ?? "",
            ImmediateDestinationName = _configuration["Nacha:ImmediateDestinationName"] ?? "",
            ImmediateOriginName = _configuration["Nacha:ImmediateOriginName"] ?? "",
            CompanyName = _configuration["Nacha:CompanyName"] ?? "",
            CompanyId = _configuration["Nacha:CompanyId"] ?? "",
            OriginatingDfi = long.TryParse(_configuration["Nacha:OriginatingDfi"], out var dfi) ? dfi : 0,
            CompanyEntryDescription = _configuration["Nacha:CreditEntryDescription"] ?? "CAPITATION"
        };
    }

    private static bool ShouldRetry(CapitationDisbursement disbursement)
    {
        if (disbursement.RetryCount >= disbursement.MaxRetries)
            return false;

        // Don't retry for account closed, unauthorized, or invalid account
        var nonRetryableCodes = new[] { "R02", "R03", "R04", "R07", "R10", "R16", "R20" };
        return !nonRetryableCodes.Contains(disbursement.ReturnCode);
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

/// <summary>
/// DTO for provider bank account data fetched from provider-service
/// </summary>
public class ProviderBankAccountDto
{
    public bool EftEnabled { get; set; }
    public string? PreferredDisbursementMethod { get; set; }
    public string? RoutingNumber { get; set; }
    public string? AccountNumber { get; set; }
    public string? AccountType { get; set; }
    public string? AccountHolderName { get; set; }
    public string? StripeConnectedAccountId { get; set; }
    public string? RoutingNumberLast4 { get; set; }
    public string? AccountNumberLast4 { get; set; }
    public bool W9OnFile { get; set; }
    public string? TaxId { get; set; }
    public string? TaxIdType { get; set; }
}
