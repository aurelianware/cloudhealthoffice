using System.Text.Json;
using CapitationService.Models;
using CapitationService.Repositories;

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
    /// Generate a NACHA credit file for all pending NACHA disbursements
    /// </summary>
    Task<NachaCreditFileResult> GenerateNachaCreditFileAsync();

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

public class CapitationDisbursementService : ICapitationDisbursementService
{
    private readonly ICapitationDisbursementRepository _disbursementRepository;
    private readonly ICapitationStatementRepository _statementRepository;
    private readonly ICapitationRunRepository _runRepository;
    private readonly INachaCreditFileService _nachaCreditFileService;
    private readonly IStripeConnectService _stripeConnectService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
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
        ILogger<CapitationDisbursementService> logger)
    {
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
        var statement = await _statementRepository.GetByIdAsync(request.StatementId)
            ?? throw new InvalidOperationException($"Statement {request.StatementId} not found");

        if (statement.Status != CapitationStatementStatus.Approved)
            throw new InvalidOperationException($"Cannot disburse against {statement.Status} statement — must be Approved");

        if (statement.NetPayable <= 0)
            throw new InvalidOperationException("Statement has no net payable amount");

        // Fetch provider bank account info
        var bankAccount = await FetchProviderBankAccountAsync(statement.ProviderNPI);
        if (bankAccount == null || !bankAccount.EftEnabled)
            throw new InvalidOperationException($"EFT not enabled for provider {statement.ProviderNPI}");

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

        // For Stripe Connect, initiate transfer immediately
        if (method == DisbursementMethod.StripeConnect)
        {
            var result = await _stripeConnectService.CreateTransferAsync(
                bankAccount.StripeConnectedAccountId!,
                amount,
                statement.StatementNumber,
                statement.ProviderNPI);

            if (result.Status == "failed")
            {
                disbursement.Status = DisbursementStatus.Failed;
                disbursement.ErrorMessage = result.ErrorMessage;
            }
            else
            {
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

        // Update statement status to PaymentInitiated
        statement.Status = CapitationStatementStatus.PaymentInitiated;
        statement.EftDisbursementId = disbursement.Id;
        await _statementRepository.UpdateAsync(statement);

        _logger.LogInformation(
            "Initiated {Method} disbursement {DisbursementId} for statement {StatementNumber}, amount ${Amount:N2}",
            method, disbursement.Id, statement.StatementNumber, amount);

        return disbursement;
    }

    public async Task<BatchDisbursementResult> InitiateBatchDisbursementAsync(InitiateBatchDisbursementRequest request)
    {
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

        var nachaEntries = new List<NachaCreditEntryDetail>();
        var nachaDisbursements = new List<CapitationDisbursement>();

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

                var bankAccount = await FetchProviderBankAccountAsync(statement.ProviderNPI);
                if (bankAccount == null || !bankAccount.EftEnabled)
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
                    // Collect for NACHA batch
                    var disbursement = new CapitationDisbursement
                    {
                        StatementId = statement.Id,
                        StatementNumber = statement.StatementNumber,
                        ProviderNPI = statement.ProviderNPI,
                        ProviderName = statement.ProviderName,
                        Amount = statement.NetPayable,
                        Method = DisbursementMethod.NachaCredit,
                        Status = DisbursementStatus.Pending,
                        RoutingNumberLast4 = bankAccount.RoutingNumberLast4,
                        AccountNumberLast4 = bankAccount.AccountNumberLast4,
                        InitiatedBy = request.InitiatedBy
                    };
                    disbursement = await _disbursementRepository.CreateAsync(disbursement);
                    nachaDisbursements.Add(disbursement);

                    nachaEntries.Add(new NachaCreditEntryDetail
                    {
                        RoutingNumber = bankAccount.RoutingNumber!,
                        AccountNumber = bankAccount.AccountNumber!,
                        AccountType = MapAccountType(bankAccount.AccountType),
                        Amount = statement.NetPayable,
                        ProviderNpi = statement.ProviderNPI,
                        IndividualName = bankAccount.AccountHolderName ?? statement.ProviderName,
                        IndividualId = statement.ProviderNPI
                    });

                    // Update statement status
                    statement.Status = CapitationStatementStatus.PaymentInitiated;
                    statement.EftDisbursementId = disbursement.Id;
                    await _statementRepository.UpdateAsync(statement);

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
            catch (Exception ex)
            {
                result.Errors++;
                result.ErrorMessages.Add($"Statement {statementId}: {ex.Message}");
                _logger.LogWarning(ex, "Failed to initiate disbursement for statement {StatementId}", statementId);
            }
        }

        // Generate NACHA credit file if there are NACHA entries
        if (nachaEntries.Count > 0)
        {
            var nachaOptions = BuildNachaCreditOptionsFromConfig();
            var nachaResult = _nachaCreditFileService.GenerateNachaCreditFile(nachaEntries, nachaOptions);
            result.NachaFile = nachaResult;

            // Update NACHA disbursements with file reference, trace numbers, mark as submitted
            for (int i = 0; i < nachaDisbursements.Count; i++)
            {
                var disbursement = nachaDisbursements[i];
                disbursement.NachaFileReference = nachaResult.FileReference;
                disbursement.Status = DisbursementStatus.Submitted;
                disbursement.SubmittedAt = DateTime.UtcNow;
                disbursement.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
                disbursement.TraceNumber = nachaEntries[i].TraceNumber;
                await _disbursementRepository.UpdateAsync(disbursement);
            }
        }

        _logger.LogInformation(
            "Batch disbursement: {Initiated}/{Total} disbursements initiated, {Skipped} skipped, {Errors} errors, ${Amount:N2} total",
            result.DisbursementsInitiated, result.TotalStatements, result.Skipped, result.Errors, result.TotalAmount);

        return result;
    }

    public async Task<NachaCreditFileResult> GenerateNachaCreditFileAsync()
    {
        var pendingDisbursements = (await _disbursementRepository.GetByStatusAsync(DisbursementStatus.Pending))
            .Where(d => d.Method == DisbursementMethod.NachaCredit)
            .ToList();

        if (pendingDisbursements.Count == 0)
            throw new InvalidOperationException("No pending NACHA credit disbursements to process");

        var entries = new List<NachaCreditEntryDetail>();
        var includedDisbursements = new List<CapitationDisbursement>();

        foreach (var disbursement in pendingDisbursements)
        {
            var bankAccount = await FetchProviderBankAccountAsync(disbursement.ProviderNPI);
            if (bankAccount?.RoutingNumber == null || bankAccount.AccountNumber == null)
            {
                _logger.LogWarning("Skipping disbursement {DisbursementId}: missing bank account for provider {NPI}",
                    disbursement.Id, disbursement.ProviderNPI);
                continue;
            }

            entries.Add(new NachaCreditEntryDetail
            {
                RoutingNumber = bankAccount.RoutingNumber,
                AccountNumber = bankAccount.AccountNumber,
                AccountType = MapAccountType(bankAccount.AccountType),
                Amount = disbursement.Amount,
                ProviderNpi = disbursement.ProviderNPI,
                IndividualName = bankAccount.AccountHolderName ?? disbursement.ProviderName,
                IndividualId = disbursement.ProviderNPI
            });
            includedDisbursements.Add(disbursement);
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("No disbursements with valid bank accounts to include in NACHA credit file");

        var nachaOptions = BuildNachaCreditOptionsFromConfig();
        var result = _nachaCreditFileService.GenerateNachaCreditFile(entries, nachaOptions);

        for (int i = 0; i < includedDisbursements.Count; i++)
        {
            var disbursement = includedDisbursements[i];
            disbursement.NachaFileReference = result.FileReference;
            disbursement.Status = DisbursementStatus.Submitted;
            disbursement.SubmittedAt = DateTime.UtcNow;
            disbursement.ExpectedSettlementDate = DateTime.UtcNow.AddBusinessDays(2);
            disbursement.TraceNumber = entries[i].TraceNumber;
            await _disbursementRepository.UpdateAsync(disbursement);
        }

        return result;
    }

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

    private async Task<ProviderBankAccountDto?> FetchProviderBankAccountAsync(string providerNpi)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("ProviderService");
            var response = await client.GetAsync($"/api/providers/npi/{providerNpi}/bank-account");
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<ProviderBankAccountDto>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch bank account for provider {NPI}", providerNpi);
            return null;
        }
    }

    private static void ValidateBankAccountForMethod(ProviderBankAccountDto bankAccount, DisbursementMethod method, string providerNpi)
    {
        if (method == DisbursementMethod.NachaCredit)
        {
            if (string.IsNullOrEmpty(bankAccount.RoutingNumber) || string.IsNullOrEmpty(bankAccount.AccountNumber))
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
