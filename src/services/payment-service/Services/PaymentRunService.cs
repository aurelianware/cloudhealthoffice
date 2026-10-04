using CloudHealthOffice.Infrastructure.Security;
using PaymentService.Models;
using PaymentService.Repositories;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentService.Services;

public interface IPaymentRunService
{
    Task<PaymentRun> CreatePaymentRunAsync(PaymentRunCriteria criteria, string? createdBy = null);
    Task<PaymentRun> ExecutePaymentRunAsync(string paymentRunId);

    /// <summary>
    /// Retries the claims-service finalize for the claims an executed run paid
    /// but claims-service has not finalized (<see cref="PaymentRun.PendingFinalizeClaimIds"/>).
    /// Idempotent: reuses each payment's check number, creates no payment.
    /// </summary>
    Task<PaymentRun> RetryFinalizeAsync(string paymentRunId);
    Task<PaymentRun> GetPaymentRunAsync(string paymentRunId);
    Task<IEnumerable<PaymentRun>> GetPaymentRunsAsync(DateTime? from = null, DateTime? to = null);
    Task CancelPaymentRunAsync(string paymentRunId);
}

public class PaymentRunService : IPaymentRunService
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IPaymentRunRepository _paymentRunRepository;
    private readonly IBatchEraGeneratorService _batchEraGenerator;
    private readonly ICarcRarcMappingService _carcRarcMapper;
    private readonly IEraEnvelopeRepository _envelopeRepository;
    private readonly ITradingPartnersClient _tradingPartnersClient;
    private readonly IClaimsServiceClient _claimsService;
    private readonly ILogger<PaymentRunService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ICurrentActor _actor;
    private readonly IRunSeparationOfDuties _separationOfDuties;

    public PaymentRunService(
        IPaymentRepository paymentRepository,
        IPaymentRunRepository paymentRunRepository,
        IBatchEraGeneratorService batchEraGenerator,
        ICarcRarcMappingService carcRarcMapper,
        IEraEnvelopeRepository envelopeRepository,
        ITradingPartnersClient tradingPartnersClient,
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentRunService> logger,
        IConfiguration configuration,
        ICurrentActor actor,
        IRunSeparationOfDuties separationOfDuties)
    {
        _paymentRepository = paymentRepository;
        _paymentRunRepository = paymentRunRepository;
        _batchEraGenerator = batchEraGenerator;
        _carcRarcMapper = carcRarcMapper;
        _envelopeRepository = envelopeRepository;
        _tradingPartnersClient = tradingPartnersClient;
        _claimsService = new ClaimsServiceClient(httpClientFactory);
        _actor = actor;
        _separationOfDuties = separationOfDuties;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<PaymentRun> CreatePaymentRunAsync(PaymentRunCriteria criteria, string? createdBy = null)
    {
        var paymentRun = new PaymentRun
        {
            PaymentRunNumber = $"PR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}",
            Criteria = criteria,
            CreatedBy = createdBy,
            Status = PaymentRunStatus.Pending,
            NextCheckNumber = await GetNextCheckNumberAsync()
        };

        var created = await _paymentRunRepository.CreateAsync(paymentRun);
        _logger.LogInformation("Created payment run {PaymentRunNumber}", created.PaymentRunNumber);
        return created;
    }

    public async Task<PaymentRun> ExecutePaymentRunAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        if (paymentRun == null)
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");

        if (paymentRun.Status != PaymentRunStatus.Pending)
            throw new InvalidOperationException($"Payment run {paymentRunId} is not in Pending status");

        // Executing issues the payments: a user other than the run's creator,
        // never a service token (throws SeparationOfDutiesException -> 403).
        var approver = _separationOfDuties.EnsureMayRelease(
            "payment run", paymentRun.PaymentRunNumber, paymentRun.CreatedBy);
        paymentRun.ExecutedBy = approver;

        // Approved: from here the run's claims-service and trading-partner calls
        // carry payment-service's service token for the run's tenant (the
        // approver holds no claims permissions). The approver stays the actor
        // on the run, the payments and the 835s.
        using var grant = RunExecutionGrant.Open(paymentRun.TenantId, paymentRun.Id, approver);

        paymentRun.Status = PaymentRunStatus.Running;
        paymentRun.ExecutionStartedAt = DateTime.UtcNow;
        await _paymentRunRepository.UpdateAsync(paymentRun);

        try
        {
            // Step 1: Fetch approved claims from claims-service, then drop every
            //         claim payment-service already paid (whatever its status
            //         in claims-service), so a failed finalize never leads to a
            //         second payment.
            var fetched = await FetchApprovedClaimsAsync(paymentRun.TenantId, paymentRun.Criteria);
            var claims = await ExcludeAlreadyPaidAsync(fetched, paymentRun);

            _logger.LogInformation(
                "Found {ClaimCount} approved claims for payment run {PaymentRunNumber}",
                claims.Count, paymentRun.PaymentRunNumber);

            if (!claims.Any())
            {
                paymentRun.Warnings.Add("No approved claims found matching criteria");
                paymentRun.Status = PaymentRunStatus.Completed;
                paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
                paymentRun.ExecutionDurationSeconds = (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds;
                return await _paymentRunRepository.UpdateAsync(paymentRun);
            }

            // Step 2: Group claims by provider (existing semantics)
            var claimGroups = GroupClaimsByProvider(claims, paymentRun.Criteria);

            // Step 3: Resolve trading partners for each unique billing
            //         provider NPI in the run. Run-scoped cache (no global
            //         singleton); credentialing-style 1-hour TTL doesn't
            //         apply here because each PaymentRun is a fresh
            //         lookup batch.
            var environment = _configuration["TradingPartners:Environment"] ?? "Production";
            var tenantId = paymentRun.TenantId;
            var resolvedTradingPartners = await ResolveTradingPartnersAsync(
                claims.Select(c => c.PayToProviderNPI ?? c.BillingProviderNPI).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal),
                tenantId,
                environment,
                paymentRun.Warnings);

            // Step 4: Allocate one check number per trading partner. Multiple
            //         provider groups under the same partner share that check
            //         so the batched envelope's TRN matches every CLP loop's
            //         finalize CheckNumber. Provider groups whose NPI doesn't
            //         resolve to a trading partner allocate their own check
            //         (legacy per-payment semantics) but are excluded from
            //         envelope emission and from finalization.
            var checkByTradingPartner = new Dictionary<string, string>(StringComparer.Ordinal);
            var checkNumberStart = paymentRun.NextCheckNumber;

            // Step 5: Generate one Payment per provider group; populate
            //         ClaimAdjustments and ServiceLine adjustments via
            //         ICarcRarcMappingService so downstream Generate835
            //         emits CAS segments correctly for denials/cost-share.
            var eraInputs = new List<EraPaymentInput>();
            var issuedPayments = new List<Payment>();
            foreach (var group in claimGroups)
            {
                var providerNpi = group.Value.First().PayToProviderNPI ?? group.Value.First().BillingProviderNPI;
                string? tradingPartnerId = null;
                if (!string.IsNullOrEmpty(providerNpi)
                    && resolvedTradingPartners.TryGetValue(providerNpi, out var partner))
                {
                    tradingPartnerId = partner.TradingPartnerId;
                }

                string checkNumber;
                if (!string.IsNullOrEmpty(tradingPartnerId))
                {
                    if (!checkByTradingPartner.TryGetValue(tradingPartnerId, out var existing))
                    {
                        existing = (paymentRun.NextCheckNumber++).ToString().PadLeft(10, '0');
                        checkByTradingPartner[tradingPartnerId] = existing;
                    }
                    checkNumber = existing;
                }
                else
                {
                    checkNumber = (paymentRun.NextCheckNumber++).ToString().PadLeft(10, '0');
                }

                var payment = await GeneratePaymentForClaimsAsync(
                    group.Value,
                    paymentRun,
                    group.Key,
                    tradingPartnerId,
                    checkNumber,
                    approver);

                issuedPayments.Add(payment);
                paymentRun.PaymentIds.Add(payment.Id);
                paymentRun.ClaimIds.AddRange(group.Value.Select(c => c.Id));
                paymentRun.TotalPaymentAmount += payment.TotalPaymentAmount;

                if (!string.IsNullOrEmpty(tradingPartnerId))
                {
                    eraInputs.Add(new EraPaymentInput { TradingPartnerId = tradingPartnerId, Payment = payment });
                }
                else
                {
                    paymentRun.Warnings.Add(
                        $"Payment {payment.CheckNumber} skipped from batched 835 — no trading partner resolved");
                }
            }

            paymentRun.TotalClaims = claims.Count;
            paymentRun.CheckNumberStart = checkNumberStart.ToString().PadLeft(10, '0');
            paymentRun.CheckNumberEnd = paymentRun.NextCheckNumber > checkNumberStart
                ? (paymentRun.NextCheckNumber - 1).ToString().PadLeft(10, '0')
                : checkNumberStart.ToString().PadLeft(10, '0');

            // Step 6: Batched 835 generation — one envelope per trading partner.
            var partnerInfos = BuildTradingPartnerInfos(resolvedTradingPartners);
            var envelopes = _batchEraGenerator.GenerateBatch(eraInputs, partnerInfos);

            // Map claim id → persisted EraEnvelope id so the finalize call can
            // carry the audit-trail crumb. Built as we persist so a retry can
            // reproduce the same association deterministically.
            var claimToEnvelopeId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var env in envelopes)
            {
                // PaymentRun envelopes always carry PaymentRunId (ReversalRunId stays null).
                var record = await _envelopeRepository.CreateAsync(new EraEnvelopeRecord
                {
                    PaymentRunId = paymentRun.Id,
                    ReversalRunId = null,
                    TradingPartnerId = env.TradingPartnerId,
                    EdiContent = env.EdiContent,
                    ClaimCount = env.ClaimCount,
                    TotalPaymentAmount = env.TotalPaymentAmount,
                    ControlNumber = env.ControlNumber,
                    ClaimIds = env.ClaimIds.ToList(),
                    CreatedBy = approver
                });
                paymentRun.EraEnvelopeIds.Add(record.Id);
                foreach (var claimId in env.ClaimIds)
                {
                    claimToEnvelopeId[claimId] = record.Id;
                }
            }

            foreach (var payment in issuedPayments)
            {
                payment.EraEnvelopeId = payment.ClaimPayments
                    .Select(cp => claimToEnvelopeId.TryGetValue(cp.ClaimId, out var envId) ? envId : null)
                    .FirstOrDefault(id => id != null);
            }

            // Step 7: Finalize each claim via the claims-service
            //         POST /api/claims/{id}/remittance endpoint. Idempotent
            //         on the server side for the same check number (5.10
            //         ClaimFinalizationService). Only claims that landed in a
            //         generated envelope are finalized. A claim that is not
            //         finalized (call failed, or no trading partner so no 835)
            //         keeps its payment PaidPendingFinalize, is listed on the
            //         run and in its errors, and is never paid again: the
            //         finalize is retried (POST {id}/finalize, or the next run
            //         that sees the claim), never re-paid.
            await FinalizeIssuedPaymentsAsync(issuedPayments, eraInputs, paymentRun);

            paymentRun.Status = PaymentRunStatus.Completed;
            paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
            paymentRun.ExecutionDurationSeconds = (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds;

            _logger.LogInformation(
                "Payment run {PaymentRunNumber} completed: {ClaimCount} claims, {PaymentCount} payments, {EnvelopeCount} envelopes, ${TotalAmount:N2}",
                paymentRun.PaymentRunNumber, paymentRun.TotalClaims, paymentRun.PaymentIds.Count,
                paymentRun.EraEnvelopeIds.Count, paymentRun.TotalPaymentAmount);

            return await _paymentRunRepository.UpdateAsync(paymentRun);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing payment run {PaymentRunId}", SanitizeForLog(paymentRunId));

            paymentRun.Status = PaymentRunStatus.Failed;
            paymentRun.Errors.Add($"Execution failed: {ex.Message}");
            paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
            paymentRun.ExecutionDurationSeconds = paymentRun.ExecutionStartedAt.HasValue
                ? (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds
                : 0;

            await _paymentRunRepository.UpdateAsync(paymentRun);
            throw;
        }
    }

    public async Task<PaymentRun> RetryFinalizeAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        if (paymentRun == null)
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");

        if (paymentRun.Status is not (PaymentRunStatus.Completed or PaymentRunStatus.Failed))
            throw new InvalidOperationException(
                $"Payment run {paymentRunId} has not been executed; only an executed run's finalizes can be retried");

        // The money was released when a second user executed the run; the retry
        // only records that release in claims-service, under that approval.
        if (string.IsNullOrWhiteSpace(paymentRun.ExecutedBy))
            throw new InvalidOperationException(
                $"Payment run {paymentRunId} records no approver; its finalizes cannot be retried");

        using var grant = RunExecutionGrant.Open(paymentRun.TenantId, paymentRun.Id, paymentRun.ExecutedBy);

        var finalized = 0;
        var stillPending = new List<string>();
        foreach (var paymentId in paymentRun.PaymentIds)
        {
            var payment = await _paymentRepository.GetByIdAsync(paymentId);
            if (payment == null || payment.IsReversal)
                continue;

            var (done, pending) = await FinalizePendingClaimsAsync(payment);
            finalized += done;
            stillPending.AddRange(pending);
        }

        paymentRun.PendingFinalizeClaimIds = stillPending.Distinct(StringComparer.Ordinal).ToList();
        paymentRun.Warnings.Add(
            $"Finalize retried by {_actor.UserId} at {DateTime.UtcNow:O}: {finalized} claim(s) finalized, " +
            $"{paymentRun.PendingFinalizeClaimIds.Count} still pending; no payment was created");

        _logger.LogInformation(
            "Finalize retry for payment run {PaymentRunNumber}: {Finalized} finalized, {Pending} pending",
            paymentRun.PaymentRunNumber, finalized, paymentRun.PendingFinalizeClaimIds.Count);

        return await _paymentRunRepository.UpdateAsync(paymentRun);
    }

    public async Task<PaymentRun> GetPaymentRunAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        if (paymentRun == null)
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");
        return paymentRun;
    }

    public async Task<IEnumerable<PaymentRun>> GetPaymentRunsAsync(DateTime? from = null, DateTime? to = null)
    {
        return await _paymentRunRepository.SearchAsync(
            from ?? DateTime.UtcNow.AddMonths(-3),
            to ?? DateTime.UtcNow);
    }

    public async Task CancelPaymentRunAsync(string paymentRunId)
    {
        var paymentRun = await _paymentRunRepository.GetByIdAsync(paymentRunId);
        if (paymentRun == null)
            throw new InvalidOperationException($"Payment run {paymentRunId} not found");

        if (paymentRun.Status == PaymentRunStatus.Running)
            throw new InvalidOperationException("Cannot cancel a running payment run");

        paymentRun.Status = PaymentRunStatus.Cancelled;
        paymentRun.CancelledBy = _actor.UserId;
        paymentRun.CancelledAt = DateTime.UtcNow;
        await _paymentRunRepository.UpdateAsync(paymentRun);
    }

    // ── Private helpers ────────────────────────────────────────────────

    private async Task<List<ClaimDto>> FetchApprovedClaimsAsync(string tenantId, PaymentRunCriteria criteria)
    {
        var queryParams = new List<string>();

        if (criteria.LineOfBusiness.HasValue)
            queryParams.Add($"lineOfBusiness={(int)criteria.LineOfBusiness.Value}");
        if (!string.IsNullOrEmpty(criteria.ProviderNPI))
            queryParams.Add($"providerNPI={criteria.ProviderNPI}");
        if (criteria.ServiceDateFrom.HasValue)
            queryParams.Add($"serviceDateFrom={criteria.ServiceDateFrom.Value:yyyy-MM-dd}");
        if (criteria.ServiceDateTo.HasValue)
            queryParams.Add($"serviceDateTo={criteria.ServiceDateTo.Value:yyyy-MM-dd}");

        // claims-service ClaimStatus.Approved == 5
        queryParams.Add("status=5");

        var queryString = string.Join("&", queryParams);
        var response = await _claimsService.SearchClaimsAsync(tenantId, $"{queryString}&pageSize=5000");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Failed to fetch claims from claims service: {response.StatusCode}");

        var claims = await response.Content.ReadFromJsonAsync<List<ClaimDto>>() ?? new List<ClaimDto>();

        // Phase 1 — apply post-fetch filters that aren't on the
        // claims-service /search endpoint surface yet. SubmissionDate
        // is the most useful for finance-cycle-tied PaymentRuns; the
        // others are operator manual-selection knobs.
        if (criteria.SubmissionDateFrom.HasValue)
            claims = claims.Where(c => !c.SubmittedDate.HasValue || c.SubmittedDate.Value >= criteria.SubmissionDateFrom.Value).ToList();
        if (criteria.SubmissionDateTo.HasValue)
            claims = claims.Where(c => !c.SubmittedDate.HasValue || c.SubmittedDate.Value <= criteria.SubmissionDateTo.Value).ToList();
        if (criteria.MinClaimAmount.HasValue)
            claims = claims.Where(c => c.TotalChargeAmount >= criteria.MinClaimAmount.Value).ToList();
        if (criteria.MaxClaimAmount.HasValue)
            claims = claims.Where(c => c.TotalChargeAmount <= criteria.MaxClaimAmount.Value).ToList();
        if (criteria.IncludeClaimIds.Any())
            claims = claims.Where(c => criteria.IncludeClaimIds.Contains(c.Id)).ToList();
        if (criteria.ExcludeClaimIds.Any())
            claims = claims.Where(c => !criteria.ExcludeClaimIds.Contains(c.Id)).ToList();
        if (criteria.MemberIds.Any())
            claims = claims.Where(c => criteria.MemberIds.Contains(c.MemberId)).ToList();

        return claims;
    }

    /// <summary>
    /// The duplicate-selection guard. Drops repeated claim ids, then every claim
    /// that already appears in a (non-reversal) payment in payment-service,
    /// whatever claims-service says its status is: a claim whose finalize failed
    /// is still Approved there, and must not be paid a second time. For such a
    /// claim whose earlier finalize is pending, the finalize is retried now
    /// (idempotent, same check number, no new payment).
    /// </summary>
    private async Task<List<ClaimDto>> ExcludeAlreadyPaidAsync(List<ClaimDto> fetched, PaymentRun paymentRun)
    {
        var unique = new List<ClaimDto>(fetched.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in fetched)
        {
            if (string.IsNullOrEmpty(claim.Id) || !seen.Add(claim.Id))
            {
                paymentRun.Warnings.Add($"Claim {claim.Id} was returned more than once; selected once");
                continue;
            }
            unique.Add(claim);
        }

        if (unique.Count == 0)
            return unique;

        var paid = await _paymentRepository.GetClaimIdsWithPaymentAsync(
            unique.Select(c => c.Id).ToList(), reversal: false) ?? Array.Empty<string>();
        var paidSet = new HashSet<string>(paid, StringComparer.Ordinal);
        if (paidSet.Count == 0)
            return unique;

        foreach (var claimId in unique.Where(c => paidSet.Contains(c.Id)).Select(c => c.Id))
        {
            paymentRun.AlreadyPaidClaimIds.Add(claimId);
            paymentRun.Warnings.Add($"Claim {claimId} already has a payment in payment-service; not paid again");
            _logger.LogWarning(
                "Claim {ClaimId} is Approved in claims-service but already paid in payment-service; excluded from run {PaymentRunNumber}",
                SanitizeForLog(claimId), paymentRun.PaymentRunNumber);

            await RetryPendingFinalizeForClaimAsync(claimId, paymentRun);
        }

        return unique.Where(c => !paidSet.Contains(c.Id)).ToList();
    }

    /// <summary>
    /// A later run found a claim an earlier run paid: if that payment's finalize
    /// failed after its 835 was emitted, finalize it now with the earlier
    /// payment's check number. Payments with no 835 (no trading partner) are
    /// left for POST {id}/finalize, where an operator decides.
    /// </summary>
    private async Task RetryPendingFinalizeForClaimAsync(string claimId, PaymentRun paymentRun)
    {
        var payments = await _paymentRepository.GetByClaimIdAsync(claimId) ?? Enumerable.Empty<Payment>();
        foreach (var payment in payments.Where(p => !p.IsReversal && !string.IsNullOrEmpty(p.EraEnvelopeId)))
        {
            var claimPayment = payment.ClaimPayments.FirstOrDefault(cp => cp.ClaimId == claimId && cp.FinalizedAt == null);
            if (claimPayment == null)
                continue;

            var ok = await TryFinalizeAsync(payment, claimPayment);
            await SavePaymentStatusAsync(payment);
            paymentRun.Warnings.Add(ok
                ? $"Claim {claimId}: pending finalize from payment run {payment.RunNumber} completed (check {payment.CheckNumber})"
                : $"Claim {claimId}: pending finalize from payment run {payment.RunNumber} failed again: {claimPayment.FinalizeError}");

            if (ok && !string.IsNullOrEmpty(payment.RunId) && payment.RunId != paymentRun.Id)
            {
                var original = await _paymentRunRepository.GetByIdAsync(payment.RunId);
                if (original != null && original.PendingFinalizeClaimIds.Remove(claimId))
                {
                    original.Warnings.Add($"Claim {claimId} finalized by payment run {paymentRun.PaymentRunNumber} at {DateTime.UtcNow:O}");
                    await _paymentRunRepository.UpdateAsync(original);
                }
            }
        }
    }

    private Dictionary<string, List<ClaimDto>> GroupClaimsByProvider(List<ClaimDto> claims, PaymentRunCriteria criteria)
    {
        if (!criteria.GroupByProvider)
            return new Dictionary<string, List<ClaimDto>> { { "ALL", claims } };

        var groups = claims.GroupBy(c => c.PayToProviderNPI ?? c.BillingProviderNPI)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (criteria.MaxClaimsPerPayment.HasValue)
        {
            var result = new Dictionary<string, List<ClaimDto>>();
            int batchNumber = 0;

            foreach (var group in groups)
            {
                var chunks = group.Value.Chunk(criteria.MaxClaimsPerPayment.Value);
                foreach (var chunk in chunks)
                {
                    result[$"{group.Key}-{++batchNumber}"] = chunk.ToList();
                }
            }

            return result;
        }

        return groups;
    }

    private async Task<Dictionary<string, TradingPartnerSummary>> ResolveTradingPartnersAsync(
        IEnumerable<string> npis,
        string tenantId,
        string environment,
        List<string> warnings)
    {
        var resolved = new Dictionary<string, TradingPartnerSummary>(StringComparer.Ordinal);

        foreach (var npi in npis)
        {
            if (resolved.ContainsKey(npi)) continue;
            var partner = await _tradingPartnersClient.GetByBillingProviderNpiAsync(tenantId, npi, environment);
            if (partner != null)
            {
                resolved[npi] = partner;
            }
            else
            {
                warnings.Add($"No trading partner configured for billing-provider NPI {npi}");
            }
        }

        return resolved;
    }

    private IReadOnlyDictionary<string, TradingPartnerInfo> BuildTradingPartnerInfos(
        Dictionary<string, TradingPartnerSummary> resolved)
    {
        var seen = new Dictionary<string, TradingPartnerInfo>(StringComparer.Ordinal);
        foreach (var partner in resolved.Values)
        {
            if (seen.ContainsKey(partner.TradingPartnerId)) continue;

            seen[partner.TradingPartnerId] = new TradingPartnerInfo
            {
                InterchangeSenderId = partner.X12Config?.SenderId
                    ?? _configuration["Era:InterchangeSenderId"] ?? "SENDER",
                InterchangeReceiverId = partner.X12Config?.ReceiverId
                    ?? _configuration["Era:InterchangeReceiverId"] ?? "RECEIVER",
                ApplicationSenderId = partner.X12Config?.SenderId
                    ?? _configuration["Era:ApplicationSenderId"] ?? "SENDER",
                ApplicationReceiverId = partner.X12Config?.ReceiverId
                    ?? _configuration["Era:ApplicationReceiverId"] ?? "RECEIVER",
                PayerRoutingNumber = _configuration["Era:PayerRoutingNumber"],
                PayerAccountNumber = _configuration["Era:PayerAccountNumber"],
                PayeeRoutingNumber = _configuration["Era:PayeeRoutingNumber"],
                PayeeAccountNumber = _configuration["Era:PayeeAccountNumber"],
            };
        }
        return seen;
    }

    private async Task<Payment> GeneratePaymentForClaimsAsync(
        List<ClaimDto> claims,
        PaymentRun paymentRun,
        string providerKey,
        string? tradingPartnerId,
        string checkNumber,
        string approver)
    {
        var firstClaim = claims.First();
        var providerNpi = firstClaim.PayToProviderNPI ?? firstClaim.BillingProviderNPI;

        var payment = new Payment
        {
            CheckNumber = checkNumber,
            PaymentMethod = paymentRun.PaymentMethod,
            TotalPaymentAmount = claims.Sum(c => c.ApprovedAmount ?? c.TotalChargeAmount),
            PaymentDate = paymentRun.PaymentDate,
            PayerName = _configuration["Payer:Name"] ?? "Cloud Health Office",
            PayerId = _configuration["Payer:Id"] ?? "CHO",
            PayeeName = firstClaim.ProviderName ?? providerKey,
            PayeeNPI = providerNpi,
            TradingPartnerId = tradingPartnerId,
            // Issued, not yet finalized in claims-service. Becomes Posted once
            // every claim in it is finalized; a crash before that leaves it
            // pending (and its claims excluded from later runs), never unpaid.
            Status = PaymentStatus.PaidPendingFinalize,
            PostedBy = approver,
            PostedAt = DateTime.UtcNow,
            RunId = paymentRun.Id,
            RunNumber = paymentRun.PaymentRunNumber,
            ClaimPayments = claims.Select(claim =>
            {
                var snapshot = BuildAdjudicationSnapshot(claim);
                var headerCas = _carcRarcMapper.MapClaimAdjustments(snapshot);
                var perLineCas = _carcRarcMapper.MapLineAdjustments(snapshot);

                var serviceLines = (claim.ServiceLines ?? new List<ClaimServiceLineDto>())
                    .Select(sl => new ServiceLinePayment
                    {
                        LineNumber = sl.LineNumber,
                        ProcedureCode = sl.ProcedureCode,
                        ChargeAmount = sl.ChargeAmount,
                        PaymentAmount = sl.PaidAmount ?? sl.ChargeAmount,
                        RevenueCode = sl.RevenueCode,
                        Units = sl.Units,
                        ServiceDateFrom = sl.ServiceDateFrom,
                        ServiceDateTo = sl.ServiceDateTo,
                        Adjustments = perLineCas.TryGetValue(sl.LineNumber, out var lineAdj)
                            ? lineAdj.ToList()
                            : new List<ServiceLineAdjustment>()
                    })
                    .ToList();

                return new ClaimPayment
                {
                    ClaimId = claim.Id,
                    PatientControlNumber = claim.ClaimNumber,
                    ClaimStatusCode = headerCas.Any(a => a.GroupCode == "CO" && claim.Status == ClaimStatus.Denied) ? "3" : "1",
                    ChargeAmount = claim.TotalChargeAmount,
                    PaymentAmount = claim.ApprovedAmount ?? claim.TotalChargeAmount,
                    PatientResponsibilityAmount = claim.PatientResponsibility ?? 0,
                    PayerClaimControlNumber = claim.PayerClaimControlNumber,
                    MemberId = claim.MemberId,
                    RenderingProviderNPI = claim.RenderingProviderNPI,
                    ClaimAdjustments = headerCas.ToList(),
                    ServiceLines = serviceLines
                };
            }).ToList()
        };

        var created = await _paymentRepository.CreateAsync(payment);
        return created;
    }

    private static ClaimAdjudicationSnapshot BuildAdjudicationSnapshot(ClaimDto claim)
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = claim.Id,
            DenialReasonCode = claim.AdjudicationResult?.DenialReasonCode,
            DenialReason = claim.AdjudicationResult?.DenialReason,
        };

        if (claim.AdjudicationResult?.AdjustmentReasons is { Count: > 0 } reasons)
        {
            snapshot.AdjustmentReasons = reasons.Select(r => new ClaimAdjustmentReasonView
            {
                GroupCode = r.GroupCode,
                ReasonCode = r.ReasonCode,
                Amount = r.Amount,
                Description = r.Description
            }).ToList();
        }

        if (claim.AdjudicationResult?.RemarkCodes is { Count: > 0 } remarks)
        {
            snapshot.RemarkCodes = remarks.ToList();
        }

        if (claim.PendDetails?.EditFailures is { Count: > 0 } failures)
        {
            snapshot.EditFailures = failures.Select(f => new EditFailureView
            {
                EditType = f.EditType,
                RuleId = f.RuleId,
                Message = f.Message,
                AffectedLineNumbers = f.AffectedLineNumbers?.ToList() ?? new List<int>(),
                SuggestedCarc = f.SuggestedCarc,
                SuggestedRarc = f.SuggestedRarc
            }).ToList();
        }

        return snapshot;
    }

    private async Task FinalizeIssuedPaymentsAsync(
        List<Payment> issuedPayments,
        List<EraPaymentInput> eraInputs,
        PaymentRun paymentRun)
    {
        var inEnvelope = new HashSet<string>(eraInputs.Select(i => i.Payment.Id), StringComparer.Ordinal);
        var pending = new List<string>();

        foreach (var payment in issuedPayments)
        {
            if (!inEnvelope.Contains(payment.Id))
            {
                // No trading partner resolved, so no 835 was emitted. The payment
                // was issued all the same: the claims stay PaidPendingFinalize
                // (never re-paid) until an operator retries the finalize.
                foreach (var cp in payment.ClaimPayments)
                {
                    cp.FinalizeError = "No trading partner resolved; no 835 emitted; not finalized";
                    pending.Add(cp.ClaimId);
                    paymentRun.Errors.Add(
                        $"Claim {cp.ClaimId} paid by check {payment.CheckNumber} but not finalized: no trading partner resolved, no 835 emitted");
                }
                await SavePaymentStatusAsync(payment);
                continue;
            }

            foreach (var cp in payment.ClaimPayments)
            {
                if (await TryFinalizeAsync(payment, cp))
                    continue;

                pending.Add(cp.ClaimId);
                paymentRun.Warnings.Add($"Finalize call for claim {cp.ClaimId} failed: {cp.FinalizeError}");
                paymentRun.Errors.Add(
                    $"Claim {cp.ClaimId} paid by check {payment.CheckNumber} but claims-service did not finalize it ({cp.FinalizeError}); " +
                    $"payment is PaidPendingFinalize; retry with POST /api/paymentruns/{paymentRun.Id}/finalize");
            }

            await SavePaymentStatusAsync(payment);
        }

        paymentRun.PendingFinalizeClaimIds = pending.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Finalizes the pending claims of one payment; returns how many were
    /// finalized and which are still pending.
    /// </summary>
    private async Task<(int Finalized, List<string> Pending)> FinalizePendingClaimsAsync(Payment payment)
    {
        var finalized = 0;
        var pending = new List<string>();
        var attempted = false;
        foreach (var cp in payment.ClaimPayments.Where(cp => cp.FinalizedAt == null))
        {
            attempted = true;
            if (await TryFinalizeAsync(payment, cp))
                finalized++;
            else
                pending.Add(cp.ClaimId);
        }

        if (attempted)
            await SavePaymentStatusAsync(payment);
        return (finalized, pending);
    }

    /// <summary>
    /// One POST /api/claims/{id}/remittance for a claim of an issued payment,
    /// with that payment's check number (claims-service treats the same check
    /// number as an idempotent no-op and a different one as 409).
    /// </summary>
    private async Task<bool> TryFinalizeAsync(Payment payment, ClaimPayment cp)
    {
        try
        {
            var body = new RemittancePostBody
            {
                ControlNumber = payment.RunNumber ?? string.Empty,
                CheckNumber = payment.CheckNumber,
                PaymentDate = payment.PaymentDate,
                PaymentAmount = cp.PaymentAmount,
                PaymentRunId = payment.RunId,
                EraEnvelopeId = payment.EraEnvelopeId
            };

            // Always inside an open grant: the run's tenant, not whatever the record carries.
            var tenantId = RunExecutionGrant.Current?.TenantId ?? payment.TenantId;
            using var response = await _claimsService.PostRemittanceAsync(tenantId, cp.ClaimId, body);
            if (response.IsSuccessStatusCode)
            {
                cp.FinalizedAt = DateTime.UtcNow;
                cp.FinalizeError = null;
                return true;
            }

            var bodyText = await response.Content.ReadAsStringAsync();
            cp.FinalizeError = $"claims-service returned {(int)response.StatusCode}";
            _logger.LogWarning(
                "Finalize call for claim {ClaimId} returned {Status}: {Body}",
                SanitizeForLog(cp.ClaimId), response.StatusCode, SanitizeForLog(bodyText));
            return false;
        }
        catch (Exception ex)
        {
            cp.FinalizeError = $"finalize call threw: {ex.Message}";
            _logger.LogError(ex, "Error finalizing claim {ClaimId}", SanitizeForLog(cp.ClaimId));
            return false;
        }
    }

    private async Task SavePaymentStatusAsync(Payment payment)
    {
        payment.Status = payment.ClaimPayments.All(cp => cp.FinalizedAt != null)
            ? PaymentStatus.Posted
            : PaymentStatus.PaidPendingFinalize;
        await _paymentRepository.UpdateAsync(payment);
    }

    private async Task<int> GetNextCheckNumberAsync()
    {
        var recentRuns = await _paymentRunRepository.SearchAsync(
            DateTime.UtcNow.AddYears(-1),
            DateTime.UtcNow);

        var lastRun = recentRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        if (lastRun != null && lastRun.NextCheckNumber > 0)
            return lastRun.NextCheckNumber;

        return int.Parse(_configuration["Payment:StartingCheckNumber"] ?? "1000000");
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// DTOs for claims service integration

public class ClaimDto
{
    public string Id { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string BillingProviderNPI { get; set; } = string.Empty;
    public string? PayToProviderNPI { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string? ProviderName { get; set; }
    public string? PayerClaimControlNumber { get; set; }
    public decimal TotalChargeAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public decimal? PatientResponsibility { get; set; }
    public ClaimStatus Status { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime? SubmittedDate { get; set; }

    /// <summary>Full claim adjudication result (5.10 — consumed by CARC/RARC mapper).</summary>
    public ClaimAdjudicationDto? AdjudicationResult { get; set; }

    /// <summary>Pend details with edit failures (5.10 — consumed by CARC/RARC mapper for per-line CAS).</summary>
    public PendDetailsDto? PendDetails { get; set; }

    /// <summary>
    /// Claim service lines (5.10 — populated into ServiceLinePayment for
    /// SVC segments). Deserialized from claims-service's
    /// <c>Claim.ClaimLines</c> property; aliased here as
    /// <c>ServiceLines</c> to keep payment-service's downstream
    /// terminology consistent with the 835 model.
    /// </summary>
    [JsonPropertyName("claimLines")]
    public List<ClaimServiceLineDto>? ServiceLines { get; set; }
}

/// <summary>Mirrors <c>ClaimsService.Models.AdjudicationResult</c> for the fields used by 5.10.</summary>
public class ClaimAdjudicationDto
{
    public decimal AllowedAmount { get; set; }
    public decimal PayerPayment { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public string? DenialReasonCode { get; set; }
    public string? DenialReason { get; set; }
    public List<ClaimAdjustmentReasonDto>? AdjustmentReasons { get; set; }
    public List<string>? RemarkCodes { get; set; }
    public string? CheckNumber { get; set; }
    public DateTime? PaymentDate { get; set; }
}

public class ClaimAdjustmentReasonDto
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Description { get; set; }
}

public class PendDetailsDto
{
    public string PendCode { get; set; } = string.Empty;
    public string? PendReason { get; set; }
    public List<EditFailureDto>? EditFailures { get; set; }
}

public class EditFailureDto
{
    public string EditType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string? Message { get; set; }
    public List<int>? AffectedLineNumbers { get; set; }
    public string? SuggestedCarc { get; set; }
    public string? SuggestedRarc { get; set; }
}

public class ClaimServiceLineDto
{
    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? RevenueCode { get; set; }
    public decimal Units { get; set; } = 1;
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
}

internal class RemittancePostBody
{
    [JsonPropertyName("controlNumber")]
    public string ControlNumber { get; set; } = string.Empty;
    [JsonPropertyName("checkNumber")]
    public string? CheckNumber { get; set; }
    [JsonPropertyName("paymentDate")]
    public DateTime PaymentDate { get; set; }
    [JsonPropertyName("paymentAmount")]
    public decimal PaymentAmount { get; set; }
    [JsonPropertyName("paymentRunId")]
    public string? PaymentRunId { get; set; }
    [JsonPropertyName("eraEnvelopeId")]
    public string? EraEnvelopeId { get; set; }
}
