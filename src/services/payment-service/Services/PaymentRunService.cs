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
    private readonly IClaimReservationRepository _reservations;

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
        IRunSeparationOfDuties separationOfDuties,
        IClaimReservationRepository reservations)
    {
        _reservations = reservations;
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
            throw new RunConflictException($"Payment run {paymentRunId} is not in Pending status");

        // Executing issues the payments: a user other than the run's creator,
        // never a service token (throws SeparationOfDutiesException -> 403).
        var approver = _separationOfDuties.EnsureMayRelease(
            "payment run", paymentRun.PaymentRunNumber, paymentRun.CreatedBy);

        // Approved: from here the run's claims-service and trading-partner calls
        // carry payment-service's service token for the run's tenant (the
        // approver holds no claims permissions). The approver stays the actor
        // on the run, the payments and the 835s.
        using var grant = RunExecutionGrant.Open(paymentRun.TenantId, paymentRun.Id, approver);

        // Pending -> Running in one conditional write: of two executors of the
        // same run exactly one gets here; the other gets 409 before any call.
        var startedAt = DateTime.UtcNow;
        if (!await _paymentRunRepository.TryStartAsync(paymentRun.Id, approver, startedAt))
            throw new RunConflictException(
                $"Payment run {paymentRunId} is already being executed or has been executed");
        paymentRun.Status = PaymentRunStatus.Running;
        paymentRun.ExecutedBy = approver;
        paymentRun.ExecutionStartedAt = startedAt;

        // Claims this run reserved but has not yet tried to pay; released if the
        // run fails before it gets to them. A claim whose payment insert was
        // attempted keeps its reservation (it may have been paid).
        var reservedNotAttempted = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            // Step 0: The 835 BPR's bank and originating-company details are
            //         payment-service configuration, the same for every
            //         partner. Check them before any claim is reserved or paid,
            //         so a misconfiguration fails the run with nothing issued
            //         rather than after payments exist without an 835.
            Era835FinancialSegments.EnsureBprCanBeBuilt(paymentRun.PaymentMethod, ConfiguredBprDetails());

            // Step 1: Fetch approved claims from claims-service, then drop every
            //         claim payment-service already paid (whatever its status
            //         in claims-service), so a failed finalize never leads to a
            //         second payment. Only claims-service Approved claims are
            //         payable, at their plan-paid amount (never billed or
            //         allowed); a claim without one is listed on the run.
            var fetched = await FetchClaimsAsync(paymentRun.TenantId, paymentRun.Criteria, ClaimStatus.Approved);
            var claims = await ExcludeAlreadyPaidAsync(fetched, paymentRun);
            claims = ExcludeNotPayable(claims, paymentRun);
            claims = ExcludeUnbalancedServiceLines(claims, paymentRun, denied: false);

            // Step 1b: Denied claims not yet remitted. They ride in the same
            //          835s as zero-pay claims (CLP02 = 4, CLP04 = 0): no
            //          payment, no reservation, no claims-service call (Denied
            //          is final there). A denial listed in an earlier 835 is
            //          not remitted again.
            var denials = paymentRun.Criteria.IncludeDeniedClaims
                ? await SelectDenialsToRemitAsync(paymentRun, fetched)
                : new List<ClaimDto>();

            // Step 2: Resolve trading partners for each unique pay-to / billing
            //         provider NPI. A claim whose provider has none is not paid:
            //         without a partner no 835 can be sent. It stays Approved in
            //         claims-service and is listed for the next run.
            var environment = _configuration["TradingPartners:Environment"] ?? "Production";
            var tenantId = paymentRun.TenantId;
            var resolvedTradingPartners = claims.Count == 0 && denials.Count == 0
                ? new Dictionary<string, TradingPartnerSummary>(StringComparer.Ordinal)
                : await ResolveTradingPartnersAsync(
                    claims.Concat(denials).Select(c => c.PayToProviderNPI ?? c.BillingProviderNPI).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal),
                    tenantId,
                    environment,
                    paymentRun.Warnings);
            claims = ExcludeWithoutTradingPartner(claims, resolvedTradingPartners, paymentRun, denied: false);
            denials = ExcludeWithoutTradingPartner(denials, resolvedTradingPartners, paymentRun, denied: true);

            // Step 3: Reserve each claim (insert-if-absent, one holder per
            //         tenant + claim). A claim another run holds, even one
            //         executing at this moment, is skipped.
            claims = await ReserveClaimsAsync(claims, paymentRun, approver);
            reservedNotAttempted.UnionWith(claims.Select(c => c.Id));

            _logger.LogInformation(
                "Found {ClaimCount} approved claims to pay for payment run {PaymentRunNumber}",
                claims.Count, paymentRun.PaymentRunNumber);

            if (!claims.Any() && !denials.Any())
            {
                paymentRun.Warnings.Add(fetched.Count == 0
                    ? "No approved claims found matching criteria"
                    : "No approved claims left to pay after excluding paid, reserved, trading-partner-less, not-approved, missing- or negative-plan-paid-amount and unbalanced-service-line claims");
                paymentRun.Status = PaymentRunStatus.Completed;
                paymentRun.ExecutionCompletedAt = DateTime.UtcNow;
                paymentRun.ExecutionDurationSeconds = (paymentRun.ExecutionCompletedAt.Value - paymentRun.ExecutionStartedAt.Value).TotalSeconds;
                return await _paymentRunRepository.UpdateAsync(paymentRun);
            }

            // Group claims by provider (existing semantics). A run that only
            // remits denials issues no payment.
            var claimGroups = claims.Count == 0
                ? new Dictionary<string, List<ClaimDto>>()
                : GroupClaimsByProvider(claims, paymentRun.Criteria);

            // Step 4: Allocate one check number per trading partner. Multiple
            //         provider groups under the same partner share that check
            //         so the batched envelope's TRN matches every CLP loop's
            //         finalize CheckNumber. Every claim left here has a
            //         trading partner (Step 2 excluded the rest).
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

                // From the insert attempt on, the reservation stays.
                reservedNotAttempted.ExceptWith(group.Value.Select(c => c.Id));
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

            // Step 5b: The denials, one zero-pay input per trading partner,
            //          after the payments so a partner's envelope keeps its
            //          payment's check number as TRN02. They add 0 to BPR02; a
            //          partner with only denials gets a NON 835 (BPR02 = 0).
            eraInputs.AddRange(BuildDenialInputs(denials, resolvedTradingPartners, checkByTradingPartner, paymentRun));

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

            // A denial is remitted once it is in a persisted 835.
            paymentRun.RemittedDeniedClaimIds.AddRange(
                denials.Select(d => d.Id).Where(claimToEnvelopeId.ContainsKey));

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
                "Payment run {PaymentRunNumber} completed: {ClaimCount} claims, {PaymentCount} payments, {DenialCount} denials remitted, {EnvelopeCount} envelopes, ${TotalAmount:N2}",
                paymentRun.PaymentRunNumber, paymentRun.TotalClaims, paymentRun.PaymentIds.Count,
                paymentRun.RemittedDeniedClaimIds.Count, paymentRun.EraEnvelopeIds.Count, paymentRun.TotalPaymentAmount);

            return await _paymentRunRepository.UpdateAsync(paymentRun);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing payment run {PaymentRunId}", SanitizeForLog(paymentRunId));

            foreach (var claimId in reservedNotAttempted)
            {
                try
                {
                    await _reservations.ReleaseAsync(ClaimReservationKind.Payment, paymentRun.TenantId, claimId, paymentRun.Id);
                }
                catch (Exception releaseEx)
                {
                    _logger.LogError(releaseEx, "Could not release the reservation of claim {ClaimId} for failed run {PaymentRunId}",
                        SanitizeForLog(claimId), SanitizeForLog(paymentRunId));
                }
            }

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

    private async Task<List<ClaimDto>> FetchClaimsAsync(string tenantId, PaymentRunCriteria criteria, ClaimStatus status)
    {
        var queryParams = new List<string>();

        if (criteria.LineOfBusiness.HasValue)
            queryParams.Add($"lineOfBusiness={ClaimsServiceLineOfBusiness(criteria.LineOfBusiness.Value)}");
        if (!string.IsNullOrEmpty(criteria.ProviderNPI))
            queryParams.Add($"providerNPI={criteria.ProviderNPI}");
        if (criteria.ServiceDateFrom.HasValue)
            queryParams.Add($"serviceDateFrom={criteria.ServiceDateFrom.Value:yyyy-MM-dd}");
        if (criteria.ServiceDateTo.HasValue)
            queryParams.Add($"serviceDateTo={criteria.ServiceDateTo.Value:yyyy-MM-dd}");

        // claims-service serializes ClaimStatus as a number; payment-service's
        // ClaimStatus mirrors its values (Approved == 5, Denied == 6).
        queryParams.Add($"status={(int)status}");

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
    /// claims-service's numeric <c>LineOfBusiness</c> for payment-service's
    /// (whose values start at 0 and are persisted on runs, so are not renumbered).
    /// </summary>
    private static int ClaimsServiceLineOfBusiness(LineOfBusiness lob) => lob switch
    {
        LineOfBusiness.Commercial => 1,
        LineOfBusiness.Medicare => 2,
        LineOfBusiness.Medicaid => 3,
        LineOfBusiness.Marketplace => 4, // claims-service Exchange
        _ => throw new ArgumentOutOfRangeException(nameof(lob), lob, "Unknown line of business"),
    };

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

    /// <summary>
    /// Keeps only claims that are payable: claims-service status Approved
    /// (the search asks for status=Approved; anything else returned — Pended,
    /// Denied, with a possibly stale payerPayment — is refused here too), and
    /// a plan-paid amount (<c>adjudicationResult.payerPayment</c>). The amount
    /// paid is the plan's payment, never the billed charge or the allowed
    /// amount. A claim without one is not reserved or paid, stays Approved in
    /// claims-service, and is listed on the run for someone to correct.
    /// </summary>
    private List<ClaimDto> ExcludeNotPayable(List<ClaimDto> claims, PaymentRun paymentRun)
    {
        var payable = new List<ClaimDto>(claims.Count);
        foreach (var claim in claims)
        {
            if (claim.Status != ClaimStatus.Approved)
            {
                paymentRun.Warnings.Add(
                    $"Claim {claim.Id} not paid: claims-service returned it with status {claim.Status}, not Approved");
                _logger.LogWarning(
                    "Claim {ClaimId} returned with status {Status}; excluded from payment run {PaymentRunNumber}",
                    SanitizeForLog(claim.Id), claim.Status, paymentRun.PaymentRunNumber);
                continue;
            }

            if (claim.PlanPaidAmount < 0m)
            {
                // A plan payment is never negative (recoupment is a reversal
                // run); claims-service can persist an arbitrary inbound amount.
                paymentRun.NegativePlanPaidClaimIds.Add(claim.Id);
                paymentRun.Warnings.Add(
                    $"Claim {claim.Id} not paid: its plan-paid amount (adjudicationResult.payerPayment) is negative ({claim.PlanPaidAmount:F2})");
                _logger.LogWarning(
                    "Claim {ClaimId} has a negative plan-paid amount; excluded from payment run {PaymentRunNumber}",
                    SanitizeForLog(claim.Id), paymentRun.PaymentRunNumber);
                continue;
            }

            if (claim.PlanPaidAmount.HasValue)
            {
                payable.Add(claim);
                continue;
            }

            paymentRun.MissingPlanPaidAmountClaimIds.Add(claim.Id);
            paymentRun.Warnings.Add(
                $"Claim {claim.Id} not paid: claims-service returned no plan-paid amount (adjudicationResult.payerPayment); it is never paid at billed charges and will be picked up once it carries an adjudication result");
            _logger.LogWarning(
                "Claim {ClaimId} has no plan-paid amount; excluded from payment run {PaymentRunNumber}",
                SanitizeForLog(claim.Id), paymentRun.PaymentRunNumber);
        }
        return payable;
    }

    /// <summary>
    /// Drops every claim whose service-line paid amounts (SVC03) do not add up
    /// to its claim payment (CLP04: the plan-paid amount, 0 for a denial): its
    /// 835 would not balance. A claim on which no line carries a paid amount
    /// was adjudicated at claim level only; it is remitted at claim level, CLP
    /// without SVC loops (005010X221A1: the 2110 loop is situational). Once
    /// any line carries a paid amount the lines are remitted, and a line with
    /// none counts as 0, which is only accepted when the other lines already
    /// make up CLP04. A dropped claim is not reserved, paid or remitted, and is
    /// listed on the run.
    /// </summary>
    private List<ClaimDto> ExcludeUnbalancedServiceLines(List<ClaimDto> claims, PaymentRun paymentRun, bool denied)
    {
        var kept = new List<ClaimDto>(claims.Count);
        foreach (var claim in claims)
        {
            var lines = claim.ServiceLines ?? new List<ClaimServiceLineDto>();
            var expected = denied ? 0m : claim.PlanPaidAmount;
            var linePaid = lines.Sum(sl => sl.LinePaidAmount ?? 0m);
            if (RemitsAtClaimLevel(claim) || linePaid == expected)
            {
                // The CAS must balance too (SVC02 - line CAS = SVC03, CLP03 -
                // all CAS = CLP04); checked here, before anything is reserved
                // or paid, rather than failing 835 generation afterwards.
                var casProblems = Era835FinancialSegments.AdjustmentBalanceProblems(BuildClaimPayment(claim, denied));
                if (casProblems.Count == 0)
                {
                    kept.Add(claim);
                    continue;
                }

                paymentRun.UnbalancedServiceLineClaimIds.Add(claim.Id);
                paymentRun.Warnings.Add(
                    (denied ? $"Denied claim {claim.Id} not remitted" : $"Claim {claim.Id} not paid") +
                    ": its adjustments do not balance, so its 835 would not balance: " + string.Join("; ", casProblems));
                _logger.LogWarning(
                    "Claim {ClaimId} adjustments do not balance; excluded from payment run {PaymentRunNumber}",
                    SanitizeForLog(claim.Id), paymentRun.PaymentRunNumber);
                continue;
            }

            paymentRun.UnbalancedServiceLineClaimIds.Add(claim.Id);
            var unpriced = lines.Count(sl => sl.LinePaidAmount is null);
            paymentRun.Warnings.Add(
                (denied ? $"Denied claim {claim.Id} not remitted" : $"Claim {claim.Id} not paid") +
                $": its service-line paid amounts total {linePaid:F2} " +
                (unpriced > 0 ? $"({unpriced} line(s) with no paid amount) " : string.Empty) +
                (denied ? "but a denial pays 0.00" : $"but its plan-paid amount is {expected:F2}") +
                ", so its 835 would not balance");
            _logger.LogWarning(
                "Claim {ClaimId} service lines do not balance to its claim payment; excluded from payment run {PaymentRunNumber}",
                SanitizeForLog(claim.Id), paymentRun.PaymentRunNumber);
        }
        return kept;
    }

    private static bool RemitsAtClaimLevel(ClaimDto claim) => Era835ClaimPaymentBuilder.RemitsAtClaimLevel(claim);

    /// <summary>
    /// The denied claims to remit in this run: claims-service status Denied,
    /// not already in a (non-reversal) 835 or a payment in payment-service
    /// (a denial is remitted once), carrying a denial reason, and with
    /// service lines that balance to 0. Denials already remitted are skipped
    /// silently (every run sees them again); the others are listed.
    /// </summary>
    private async Task<List<ClaimDto>> SelectDenialsToRemitAsync(PaymentRun paymentRun, List<ClaimDto> approvedFetched)
    {
        var fetched = await FetchClaimsAsync(paymentRun.TenantId, paymentRun.Criteria, ClaimStatus.Denied);

        var approvedIds = new HashSet<string>(approvedFetched.Select(c => c.Id), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<ClaimDto>(fetched.Count);
        foreach (var claim in fetched)
        {
            if (string.IsNullOrEmpty(claim.Id) || !seen.Add(claim.Id) || approvedIds.Contains(claim.Id))
                continue;
            if (claim.Status != ClaimStatus.Denied)
            {
                paymentRun.Warnings.Add(
                    $"Claim {claim.Id} not remitted as a denial: claims-service returned it with status {claim.Status}, not Denied");
                continue;
            }
            candidates.Add(claim);
        }

        if (candidates.Count == 0)
            return candidates;

        var ids = candidates.Select(c => c.Id).ToList();
        var remitted = new HashSet<string>(
            await _envelopeRepository.GetClaimIdsWithEnvelopeAsync(ids, reversal: false) ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        remitted.UnionWith(
            await _paymentRepository.GetClaimIdsWithPaymentAsync(ids, reversal: false) ?? Array.Empty<string>());
        if (remitted.Count > 0)
        {
            _logger.LogDebug(
                "{Count} denied claims already remitted; not remitted again by payment run {PaymentRunNumber}",
                remitted.Count, paymentRun.PaymentRunNumber);
        }

        var withReason = new List<ClaimDto>(candidates.Count);
        foreach (var claim in candidates.Where(c => !remitted.Contains(c.Id)))
        {
            if (!string.IsNullOrWhiteSpace(claim.AdjudicationResult?.DenialReasonCode)
                || claim.AdjudicationResult?.AdjustmentReasons is { Count: > 0 })
            {
                withReason.Add(claim);
                continue;
            }

            paymentRun.DeniedWithoutReasonClaimIds.Add(claim.Id);
            paymentRun.Warnings.Add(
                $"Denied claim {claim.Id} not remitted: claims-service returned no denial reason code or adjustment reason, and an 835 denial needs a CARC");
        }

        return ExcludeUnbalancedServiceLines(withReason, paymentRun, denied: true);
    }

    private List<ClaimDto> ExcludeWithoutTradingPartner(
        List<ClaimDto> claims, IReadOnlyDictionary<string, TradingPartnerSummary> resolved, PaymentRun paymentRun, bool denied)
    {
        var payable = new List<ClaimDto>(claims.Count);
        foreach (var claim in claims)
        {
            var npi = claim.PayToProviderNPI ?? claim.BillingProviderNPI;
            if (!string.IsNullOrEmpty(npi) && resolved.ContainsKey(npi))
            {
                payable.Add(claim);
                continue;
            }

            paymentRun.NeedsTradingPartnerClaimIds.Add(claim.Id);
            paymentRun.Warnings.Add(
                (denied ? $"Denied claim {claim.Id} not remitted" : $"Claim {claim.Id} not paid") +
                $": provider NPI {npi} has no trading partner, so no 835 can be sent; it will be picked up once one is configured");
        }
        return payable;
    }

    private async Task<List<ClaimDto>> ReserveClaimsAsync(List<ClaimDto> claims, PaymentRun paymentRun, string approver)
    {
        var reserved = new List<ClaimDto>(claims.Count);
        foreach (var claim in claims)
        {
            var ok = await _reservations.TryReserveAsync(new ClaimReservation
            {
                TenantId = paymentRun.TenantId,
                Kind = ClaimReservationKind.Payment,
                ClaimId = claim.Id,
                RunId = paymentRun.Id,
                RunNumber = paymentRun.PaymentRunNumber,
                ReservedBy = approver,
                ReservedAt = DateTime.UtcNow,
            });
            if (ok)
            {
                reserved.Add(claim);
                continue;
            }

            paymentRun.AlreadyPaidClaimIds.Add(claim.Id);
            paymentRun.Warnings.Add($"Claim {claim.Id} is already reserved for payment by another run; not paid again");
        }
        return reserved;
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
        var bpr = ConfiguredBprDetails();
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
                PayerRoutingNumber = bpr.PayerRoutingNumber,
                PayerAccountNumber = bpr.PayerAccountNumber,
                OriginatingCompanyId = bpr.OriginatingCompanyId,
                OriginatingCompanySupplementalCode = bpr.OriginatingCompanySupplementalCode,
                PayeeRoutingNumber = bpr.PayeeRoutingNumber,
                PayeeAccountNumber = bpr.PayeeAccountNumber,
            };
        }
        return seen;
    }

    /// <summary>The BPR bank and originating-company details from configuration (Era:*).</summary>
    private TradingPartnerInfo ConfiguredBprDetails() => new()
    {
        PayerRoutingNumber = _configuration["Era:PayerRoutingNumber"],
        PayerAccountNumber = _configuration["Era:PayerAccountNumber"],
        OriginatingCompanyId = _configuration["Era:OriginatingCompanyId"],
        OriginatingCompanySupplementalCode = _configuration["Era:OriginatingCompanySupplementalCode"],
        PayeeRoutingNumber = _configuration["Era:PayeeRoutingNumber"],
        PayeeAccountNumber = _configuration["Era:PayeeAccountNumber"],
    };

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
            TotalPaymentAmount = claims.Sum(PlanPaidAmountOf),
            PaymentDate = paymentRun.PaymentDate,
            PayerName = _configuration["Payer:Name"] ?? "Cloud Health Office",
            PayerId = _configuration["Payer:Id"] ?? "CHO",
            PayeeName = firstClaim.PayeeNameOr(providerKey),
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
            ClaimPayments = claims.Select(claim => BuildClaimPayment(claim, denied: false)).ToList()
        };

        var created = await _paymentRepository.CreateAsync(payment);
        return created;
    }

    /// <summary>
    /// The 835 inputs for the run's denials: per trading partner, one Payment
    /// that is never persisted (no money moves, nothing to finalize or
    /// reverse) carrying the denials as zero-pay claims. TRN02 is the
    /// partner's check number when the run pays it something, else a trace
    /// number derived from the run number.
    /// </summary>
    private List<EraPaymentInput> BuildDenialInputs(
        List<ClaimDto> denials,
        IReadOnlyDictionary<string, TradingPartnerSummary> resolved,
        IReadOnlyDictionary<string, string> checkByTradingPartner,
        PaymentRun paymentRun)
    {
        var inputs = new List<EraPaymentInput>();
        var traceSequence = 0;
        var byPartner = denials
            .Select(d => (Claim: d, Npi: d.PayToProviderNPI ?? d.BillingProviderNPI))
            .Where(x => !string.IsNullOrEmpty(x.Npi) && resolved.ContainsKey(x.Npi))
            .GroupBy(x => resolved[x.Npi].TradingPartnerId, StringComparer.Ordinal);

        foreach (var group in byPartner)
        {
            var first = group.First();
            var trace = checkByTradingPartner.TryGetValue(group.Key, out var check)
                ? check
                : $"{paymentRun.PaymentRunNumber}-D{++traceSequence}";

            inputs.Add(new EraPaymentInput
            {
                TradingPartnerId = group.Key,
                Payment = new Payment
                {
                    CheckNumber = trace,
                    PaymentMethod = paymentRun.PaymentMethod,
                    TotalPaymentAmount = 0m,
                    PaymentDate = paymentRun.PaymentDate,
                    PayerName = _configuration["Payer:Name"] ?? "Cloud Health Office",
                    PayerId = _configuration["Payer:Id"] ?? "CHO",
                    PayeeName = first.Claim.PayeeNameOr(first.Npi),
                    PayeeNPI = first.Npi,
                    TradingPartnerId = group.Key,
                    RunId = paymentRun.Id,
                    RunNumber = paymentRun.PaymentRunNumber,
                    ClaimPayments = group.Select(x => BuildClaimPayment(x.Claim, denied: true)).ToList(),
                },
            });
        }
        return inputs;
    }

    /// <summary>One claim's 2100/2110 data; see <see cref="Era835ClaimPaymentBuilder"/>.</summary>
    private ClaimPayment BuildClaimPayment(ClaimDto claim, bool denied) =>
        Era835ClaimPaymentBuilder.Build(claim, denied, _carcRarcMapper);

    private static decimal PlanPaidAmountOf(ClaimDto claim) => Era835ClaimPaymentBuilder.PlanPaidAmountOf(claim);

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

    /// <summary>
    /// The billing provider's name (claims-service <c>Claim.BillingProviderName</c>,
    /// sent as <c>billingProviderName</c>): the payee name in N1*PE (1000B).
    /// Null when the 837 carried none; see <see cref="PayeeNameOr"/>.
    /// </summary>
    [JsonPropertyName("billingProviderName")]
    public string? ProviderName { get; set; }

    /// <summary>The N1*PE payee name: <see cref="ProviderName"/>, or <paramref name="fallback"/> when it is blank.</summary>
    public string PayeeNameOr(string fallback) =>
        string.IsNullOrWhiteSpace(ProviderName) ? fallback : ProviderName.Trim();

    public string? PayerClaimControlNumber { get; set; }

    /// <summary>
    /// claims-service <c>Claim.ClaimFrequencyCode</c> (837 CLM05-3: 1 original,
    /// 7 replacement, 8 void). 835 CLP09 for an institutional claim.
    /// </summary>
    public string? ClaimFrequencyCode { get; set; }

    /// <summary>
    /// claims-service <c>Claim.Institutional</c> (837I header detail); null on
    /// professional and dental claims. Supplies CLP08 (facility type code) and
    /// CLP11 (DRG).
    /// </summary>
    public InstitutionalClaimDto? Institutional { get; set; }
    /// <summary>CLP03: the claim's total billed charge. Never the amount paid.</summary>
    public decimal TotalChargeAmount { get; set; }

    /// <summary>
    /// The amount the plan pays the provider (CLP04): claims-service's
    /// <c>adjudicationResult.payerPayment</c>, the amount it publishes as
    /// PlanPaid and finalizes. Not the allowed amount (which includes member
    /// cost share) and never the billed charge. Null when the claim carries no
    /// adjudication result or no payer payment; such a claim is not paid.
    /// </summary>
    [JsonIgnore]
    public decimal? PlanPaidAmount => AdjudicationResult?.PayerPayment;

    public ClaimStatus Status { get; set; }

    /// <summary>claims-service's <c>ClaimType</c> (numeric): 837P, 837I or 837D. Decides MIA vs MOA.</summary>
    public ClaimFormType ClaimType { get; set; } = ClaimFormType.Professional;

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

/// <summary>
/// Mirrors <c>ClaimsService.Models.InstitutionalClaimDetails</c> for the fields
/// the 835 CLP segment reports.
/// </summary>
public class InstitutionalClaimDto
{
    /// <summary>Facility type code, the first two digits of the type of bill (837I CLM05-1): CLP08.</summary>
    public string? FacilityTypeCode { get; set; }

    /// <summary>The DRG billed on the claim (837I HI*DR): CLP11.</summary>
    public string? DrgCode { get; set; }
}

/// <summary>Mirrors claims-service's <c>ClaimType</c> value for value.</summary>
public enum ClaimFormType
{
    Professional = 1,
    Institutional = 2,
    Dental = 3,
}

/// <summary>Mirrors <c>ClaimsService.Models.AdjudicationResult</c> for the fields used by 5.10.</summary>
public class ClaimAdjudicationDto
{
    public decimal AllowedAmount { get; set; }
    /// <summary>What the plan pays the provider; null when claims-service did not send it.</summary>
    public decimal? PayerPayment { get; set; }
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
    /// <summary>
    /// The line's paid amount (SVC03): <see cref="PaidAmount"/> when sent,
    /// otherwise claims-service's <c>ClaimLine.AdjudicationResult.PaidAmount</c>.
    /// Null when neither is present; never the charge.
    /// </summary>
    [JsonIgnore]
    public decimal? LinePaidAmount => PaidAmount ?? AdjudicationResult?.PaidAmount;

    /// <summary>Mirrors <c>ClaimsService.Models.LineAdjudicationResult</c> for the paid amount.</summary>
    public ClaimLineAdjudicationDto? AdjudicationResult { get; set; }

    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    /// <summary>claims-service <c>ClaimLine.Modifiers</c> as billed; reported in SVC01.</summary>
    public List<string>? Modifiers { get; set; }
    public decimal ChargeAmount { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? RevenueCode { get; set; }
    public decimal Units { get; set; } = 1;
    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }
}

public class ClaimLineAdjudicationDto
{
    public decimal? PaidAmount { get; set; }

    /// <summary>
    /// The line's adjustments (claims-service <c>LineAdjudicationResult.AdjustmentReasons</c>):
    /// CO-45, PR-1/2/3, OA-23, CO denial CARCs. Emitted as the line's CAS.
    /// Empty on claims adjudicated before claims-service populated it.
    /// </summary>
    public List<ClaimLineAdjustmentReasonDto>? AdjustmentReasons { get; set; }
}

/// <summary>
/// One line adjustment as claims-service sends it. <c>remarkCode</c> is read
/// when present (claims-service adds it with line cost-share adjustments) and
/// is null otherwise.
/// </summary>
public class ClaimLineAdjustmentReasonDto
{
    public string GroupCode { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public string? RemarkCode { get; set; }
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
