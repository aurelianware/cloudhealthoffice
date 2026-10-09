using CloudHealthOffice.Infrastructure.Security;
using PaymentService.Models;
using PaymentService.Repositories;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaymentService.Services;

/// <summary>
/// Operator-initiated 835 reversal batch service (capability 5.12b).
/// Mirrors <see cref="IPaymentRunService"/> structurally — the second
/// instance of the operator-initiated batch workflow pattern in
/// payment-service. Consumes the 5.12a
/// <c>GET /api/v1/adjustments?status=PendingReversal</c> surface to
/// materialize a batch, then for each adjustment:
/// <list type="bullet">
///   <item><description>Constructs a negative-amount <see cref="Payment"/> from the predecessor's recorded claim payment via <see cref="Era835ClaimPaymentBuilder.BuildReversal"/>: CLP02 = "22", CLP03/04/05, SVC02/03 and every claim and line CAS amount negated, so each line and the claim balance in the negative.</description></item>
///   <item><description>Generates one reversal 835 envelope per trading partner via <see cref="IBatchEraGeneratorService"/>; persists with <see cref="EraEnvelopeRecord.ReversalRunId"/> set.</description></item>
///   <item><description>Calls <c>POST /api/claims/{id}/void</c> on claims-service; the claims-service hook transitions the originating adjustment <c>PendingReversal → Active</c> on success.</description></item>
/// </list>
/// </summary>
public interface IReversalRunService
{
    Task<ReversalRun> CreateReversalRunAsync(ReversalRunCriteria criteria, string? createdBy = null, string? description = null);
    Task<ReversalRun> ExecuteReversalRunAsync(string reversalRunId);

    /// <summary>
    /// Retries the claims-service void for the predecessor claims an executed
    /// reversal run recouped but claims-service has not voided
    /// (<see cref="ReversalRun.PendingVoidClaimIds"/>). Idempotent in
    /// claims-service (AlreadyVoided is 200); creates no reversal payment.
    /// </summary>
    Task<ReversalRun> RetryVoidsAsync(string reversalRunId);
    Task<ReversalRun> GetReversalRunAsync(string reversalRunId);
    Task<IEnumerable<ReversalRun>> GetReversalRunsAsync(DateTime? from = null, DateTime? to = null);
    Task CancelReversalRunAsync(string reversalRunId);
}

public class ReversalRunService : IReversalRunService
{
    /// <summary>Reversal (recoupment) payments are ACH; their 835 BPR is built for ACH.</summary>
    private const string ReversalPaymentMethod = "ACH";

    private readonly IPaymentRepository _paymentRepository;
    private readonly IReversalRunRepository _reversalRunRepository;
    private readonly IBatchEraGeneratorService _batchEraGenerator;
    private readonly IEraEnvelopeRepository _envelopeRepository;
    private readonly ITradingPartnersClient _tradingPartnersClient;
    private readonly IClaimsServiceClient _claimsService;
    private readonly ILogger<ReversalRunService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ICurrentActor _actor;
    private readonly IRunSeparationOfDuties _separationOfDuties;
    private readonly IClaimReservationRepository _reservations;
    private readonly ICarcRarcMappingService _carcRarcMapper;
    private readonly IProviderReceivableLedger? _receivables;

    public ReversalRunService(
        IPaymentRepository paymentRepository,
        IReversalRunRepository reversalRunRepository,
        IBatchEraGeneratorService batchEraGenerator,
        IEraEnvelopeRepository envelopeRepository,
        ITradingPartnersClient tradingPartnersClient,
        IHttpClientFactory httpClientFactory,
        ILogger<ReversalRunService> logger,
        IConfiguration configuration,
        ICurrentActor actor,
        IRunSeparationOfDuties separationOfDuties,
        IClaimReservationRepository reservations,
        ICarcRarcMappingService carcRarcMapper,
        IProviderReceivableLedger? receivables = null)
    {
        _reservations = reservations;
        _carcRarcMapper = carcRarcMapper;
        _receivables = receivables;
        _paymentRepository = paymentRepository;
        _reversalRunRepository = reversalRunRepository;
        _batchEraGenerator = batchEraGenerator;
        _envelopeRepository = envelopeRepository;
        _tradingPartnersClient = tradingPartnersClient;
        _claimsService = new ClaimsServiceClient(httpClientFactory);
        _actor = actor;
        _separationOfDuties = separationOfDuties;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<ReversalRun> CreateReversalRunAsync(
        ReversalRunCriteria criteria,
        string? createdBy = null,
        string? description = null)
    {
        var run = new ReversalRun
        {
            ReversalRunNumber = $"RR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}",
            Criteria = criteria,
            Description = description,
            CreatedBy = createdBy,
            Status = ReversalRunStatus.Pending,
        };
        var created = await _reversalRunRepository.CreateAsync(run);
        _logger.LogInformation("Created reversal run {ReversalRunNumber}", created.ReversalRunNumber);
        return created;
    }

    public async Task<ReversalRun> ExecuteReversalRunAsync(string reversalRunId)
    {
        var run = await _reversalRunRepository.GetByIdAsync(reversalRunId);
        if (run == null)
            throw new InvalidOperationException($"Reversal run {reversalRunId} not found");

        if (run.Status != ReversalRunStatus.Pending)
            throw new RunConflictException($"Reversal run {reversalRunId} is not in Pending status");

        // Executing recoups payments and voids claims: a user other than the
        // run's creator, never a service token (SeparationOfDutiesException -> 403).
        var approver = _separationOfDuties.EnsureMayRelease(
            "reversal run", run.ReversalRunNumber, run.CreatedBy);

        // Approved: claims-service and trading-partner calls for this run carry
        // payment-service's service token for the run's tenant; the approver is
        // recorded on the run, the reversal payments, the 835s and the void reason.
        using var grant = RunExecutionGrant.Open(run.TenantId, run.Id, approver);

        // Pending -> Running in one conditional write; a second executor gets 409.
        var startedAt = DateTime.UtcNow;
        if (!await _reversalRunRepository.TryStartAsync(run.Id, approver, startedAt))
            throw new RunConflictException(
                $"Reversal run {reversalRunId} is already being executed or has been executed");
        run.Status = ReversalRunStatus.Running;
        run.ExecutedBy = approver;
        run.ExecutionStartedAt = startedAt;

        try
        {
            // Step 0 — the reversal 835 BPR's bank and originating-company
            //          details are configuration: check them before any claim
            //          is reserved or recouped (reversal payments are ACH).
            Era835FinancialSegments.EnsureBprCanBeBuilt(ReversalPaymentMethod, new TradingPartnerInfo
            {
                PayerRoutingNumber = _configuration["Era:PayerRoutingNumber"],
                PayerAccountNumber = _configuration["Era:PayerAccountNumber"],
                OriginatingCompanyId = _configuration["Era:OriginatingCompanyId"],
                OriginatingCompanySupplementalCode = _configuration["Era:OriginatingCompanySupplementalCode"],
                PayeeRoutingNumber = _configuration["Era:PayeeRoutingNumber"],
                PayeeAccountNumber = _configuration["Era:PayeeAccountNumber"],
            });

            // Step 1 — fetch the PendingReversal adjustment batch from
            //          claims-service. The 5.12a list endpoint already
            //          supports the filter shape we need (status +
            //          createdBy + date range + pagination).
            var adjustments = await FetchPendingReversalAdjustmentsAsync(run.TenantId, run.Criteria);

            _logger.LogInformation(
                "Reversal run {ReversalRunNumber} found {Count} PendingReversal adjustments",
                run.ReversalRunNumber, adjustments.Count);

            if (adjustments.Count == 0)
            {
                run.Warnings.Add("No PendingReversal adjustments matched criteria");
                run.Status = ReversalRunStatus.Completed;
                run.ExecutionCompletedAt = DateTime.UtcNow;
                run.ExecutionDurationSeconds = (run.ExecutionCompletedAt.Value - run.ExecutionStartedAt!.Value).TotalSeconds;
                return await _reversalRunRepository.UpdateAsync(run);
            }

            // Step 2 — fetch the predecessor claim for each adjustment.
            //          We need its AdjudicationResult / ClaimLines /
            //          billing-provider NPI to build the sign-flipped
            //          reversal Payment. The fetch also lets us apply
            //          ProviderNPI post-filter (the claims-service list
            //          endpoint doesn't natively filter by predecessor
            //          NPI).
            var predecessors = new Dictionary<string, ClaimDto>(StringComparer.Ordinal);
            foreach (var adj in adjustments)
            {
                if (predecessors.ContainsKey(adj.PredecessorClaimId)) continue;
                var pred = await FetchClaimAsync(run.TenantId, adj.PredecessorClaimId);
                if (pred is null)
                {
                    run.Warnings.Add($"Predecessor claim {adj.PredecessorClaimId} not found; adjustment {adj.Id} skipped");
                    continue;
                }
                predecessors[adj.PredecessorClaimId] = pred;
            }

            // Step 2b — apply ProviderNPI post-filter. Adjustments whose
            //           predecessor NPI doesn't match are dropped from the
            //           batch silently (operator-supplied filter; not a
            //           warning condition).
            if (!string.IsNullOrEmpty(run.Criteria.ProviderNPI))
            {
                var npi = run.Criteria.ProviderNPI;
                adjustments = adjustments
                    .Where(a => predecessors.TryGetValue(a.PredecessorClaimId, out var p)
                        && string.Equals(p.PayeeNpi, npi, StringComparison.Ordinal))
                    .ToList();
                if (adjustments.Count == 0)
                {
                    run.Warnings.Add($"No PendingReversal adjustments matched ProviderNPI={npi}");
                    run.Status = ReversalRunStatus.Completed;
                    run.ExecutionCompletedAt = DateTime.UtcNow;
                    run.ExecutionDurationSeconds = (run.ExecutionCompletedAt.Value - run.ExecutionStartedAt!.Value).TotalSeconds;
                    return await _reversalRunRepository.UpdateAsync(run);
                }
            }

            // Step 3 — resolve trading partners for the surviving batch,
            //          mirroring 5.10 PaymentRunService.
            var environment = _configuration["TradingPartners:Environment"] ?? "Production";
            var resolvedTradingPartners = await ResolveTradingPartnersAsync(
                predecessors.Values
                    .Select(c => c.PayeeNpi)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.Ordinal),
                run.TenantId,
                environment,
                run.Warnings);

            // Step 3b — never reverse a claim twice. payment-service's own
            //           records are authoritative: a predecessor that already
            //           has a reversal payment (its void may have failed, so
            //           its adjustment is still PendingReversal) gets no new
            //           reversal payment; a pending void is retried instead.
            var alreadyReversed = new HashSet<string>(
                await _paymentRepository.GetClaimIdsWithPaymentAsync(
                    predecessors.Keys.ToList(), reversal: true) ?? Array.Empty<string>(),
                StringComparer.Ordinal);
            var reversedThisRun = new HashSet<string>(StringComparer.Ordinal);

            // Step 4 — construct one reversal Payment per adjustment;
            //          group by trading partner for envelope generation.
            var eraInputs = new List<EraPaymentInput>();
            var adjustmentsToVoid = new List<(ClaimAdjustmentDto Adjustment, ClaimDto Predecessor, Payment Payment)>();
            var issuedPayments = new List<Payment>();
            foreach (var adj in adjustments)
            {
                if (!predecessors.TryGetValue(adj.PredecessorClaimId, out var pred))
                    continue;

                if (alreadyReversed.Contains(pred.Id))
                {
                    run.AlreadyReversedClaimIds.Add(pred.Id);
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} (adjustment {adj.Id}) already has a reversal payment in payment-service; not reversed again");
                    _logger.LogWarning(
                        "Predecessor {ClaimId} already reversed in payment-service; adjustment {AdjustmentId} not reversed again by {ReversalRunNumber}",
                        SanitizeForLog(pred.Id), SanitizeForLog(adj.Id), run.ReversalRunNumber);
                    await RetryPendingVoidForClaimAsync(pred.Id, adj, run);
                    continue;
                }

                if (!reversedThisRun.Add(pred.Id))
                {
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} appears in more than one adjustment; adjustment {adj.Id} not reversed again");
                    continue;
                }

                var providerNpi = pred.PayeeNpi;
                string? tradingPartnerId = null;
                if (!string.IsNullOrEmpty(providerNpi)
                    && resolvedTradingPartners.TryGetValue(providerNpi, out var partner))
                {
                    tradingPartnerId = partner.TradingPartnerId;
                }

                // No trading partner, no reversal 835: not recouped. The
                // adjustment stays PendingReversal for a later run.
                if (string.IsNullOrEmpty(tradingPartnerId))
                {
                    run.NeedsTradingPartnerClaimIds.Add(pred.Id);
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} (adjustment {adj.Id}) not reversed: provider NPI {providerNpi} has no trading partner, so no reversal 835 can be sent");
                    continue;
                }

                // A reversal recoups what was actually paid: the claim payment
                // payment-service recorded for the predecessor, never its
                // approved or billed amount. No recorded payment (or an
                // unbalanced one): not reversed, listed for an operator.
                var (original, notReversible) = await FindOriginalClaimPaymentAsync(pred.Id);
                if (original is null)
                {
                    run.MissingPaidAmountClaimIds.Add(pred.Id);
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} (adjustment {adj.Id}) not reversed: {notReversible}; a reversal is never computed from approved or billed amounts");
                    _logger.LogWarning(
                        "Predecessor {ClaimId} has no single recorded payment; adjustment {AdjustmentId} not reversed by {ReversalRunNumber}",
                        SanitizeForLog(pred.Id), SanitizeForLog(adj.Id), run.ReversalRunNumber);
                    continue;
                }
                if (Era835FinancialSegments.ServiceLineBalanceProblem(original) is { } unbalanced)
                {
                    run.UnbalancedServiceLineClaimIds.Add(pred.Id);
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} (adjustment {adj.Id}) not reversed: its recorded payment does not balance ({unbalanced}), so its reversal 835 would not balance");
                    continue;
                }

                // The reversal's 2100/2110 loops: the original's, every
                // amount negated. Each line and the claim must balance (the
                // check 835 generation applies), or it is not reversed.
                var reversalClaim = Era835ClaimPaymentBuilder.BuildReversal(original, pred, _carcRarcMapper);
                var casProblems = Era835FinancialSegments.AdjustmentBalanceProblems(reversalClaim);
                if (casProblems.Count > 0)
                {
                    run.UnbalancedServiceLineClaimIds.Add(pred.Id);
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} (adjustment {adj.Id}) not reversed: its reversal's adjustments do not balance ({string.Join("; ", casProblems)})");
                    continue;
                }

                // One reversal per claim, even across concurrent runs.
                var reservedNow = await _reservations.TryReserveAsync(new ClaimReservation
                {
                    TenantId = run.TenantId,
                    Kind = ClaimReservationKind.Reversal,
                    ClaimId = pred.Id,
                    RunId = run.Id,
                    RunNumber = run.ReversalRunNumber,
                    ReservedBy = approver,
                    ReservedAt = DateTime.UtcNow,
                });
                if (!reservedNow)
                {
                    run.AlreadyReversedClaimIds.Add(pred.Id);
                    run.Warnings.Add(
                        $"Predecessor {pred.Id} (adjustment {adj.Id}) is already reserved for reversal by another run; not reversed again");
                    continue;
                }

                var checkNumber = $"R-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}";
                var payment = await BuildReversalPaymentAsync(pred, reversalClaim, run, tradingPartnerId, checkNumber, approver);

                issuedPayments.Add(payment);
                run.PaymentIds.Add(payment.Id);
                run.TotalReversalAmount += payment.TotalPaymentAmount;

                if (!string.IsNullOrEmpty(tradingPartnerId))
                {
                    eraInputs.Add(new EraPaymentInput
                    {
                        TradingPartnerId = tradingPartnerId,
                        Payment = payment,
                        IsReversal = true,
                    });
                    adjustmentsToVoid.Add((adj, pred, payment));
                }
                else
                {
                    run.Warnings.Add(
                        $"Reversal payment {payment.CheckNumber} for adjustment {adj.Id} skipped from envelope — no trading partner resolved");
                    // Recouped all the same: never reversed again; the void
                    // waits for an operator (POST /api/reversalruns/{id}/void).
                    foreach (var cp in payment.ClaimPayments)
                        cp.FinalizeError = "No trading partner resolved; no reversal 835 emitted; not voided";
                    run.PendingVoidClaimIds.Add(pred.Id);
                    run.Errors.Add(
                        $"Predecessor {pred.Id} recouped by {payment.CheckNumber} but not voided: no trading partner resolved, no reversal 835 emitted");
                    await SavePaymentStatusAsync(payment);
                }
            }

            run.TotalAdjustments = adjustments.Count;

            // Step 5 — batched 835 reversal generation. CLP02="22" was set
            //          upstream when constructing the Payment; charge, paid
            //          and CAS amounts (claim and line) are negated on each
            //          ClaimPayment.
            var partnerInfos = BuildTradingPartnerInfos(resolvedTradingPartners);
            var envelopes = _batchEraGenerator.GenerateBatch(eraInputs, partnerInfos);

            var claimToEnvelopeId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var env in envelopes)
            {
                var record = await _envelopeRepository.CreateAsync(new EraEnvelopeRecord
                {
                    PaymentRunId = string.Empty,
                    ReversalRunId = run.Id,
                    TradingPartnerId = env.TradingPartnerId,
                    EdiContent = env.EdiContent,
                    ClaimCount = env.ClaimCount,
                    TotalPaymentAmount = env.TotalPaymentAmount,
                    ForwardBalanceAmount = env.ForwardBalanceAmount,
                    ControlNumber = env.ControlNumber,
                    ClaimIds = env.ClaimIds.ToList(),
                    CreatedBy = approver,
                });
                run.EraEnvelopeIds.Add(record.Id);

                // A recoupment nets the 835 below zero: BPR02 = 0 and the
                // balance is carried forward (PLB FB). The amount owed opens a
                // provider receivable in the ledger, keyed by this 835, which
                // later payment runs recover from the provider's payments.
                if (env.ForwardBalanceAmount < 0m)
                    await RecordForwardBalancesAsync(env, record.Id, eraInputs, run, approver);
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

            // Step 6 — call the 5.12b void endpoint per adjustment.
            //          Idempotent on the server side (AlreadyVoided →
            //          200 OK with no event re-emit). Per-adjustment
            //          warnings keep the run progressing on partial
            //          failure (Decision 4).
            await VoidPredecessorsAsync(adjustmentsToVoid, run, claimToEnvelopeId);

            run.Status = ReversalRunStatus.Completed;
            run.ExecutionCompletedAt = DateTime.UtcNow;
            run.ExecutionDurationSeconds = (run.ExecutionCompletedAt.Value - run.ExecutionStartedAt!.Value).TotalSeconds;

            _logger.LogInformation(
                "Reversal run {ReversalRunNumber} completed: {Adjustments} adjustments, {Envelopes} envelopes, ${Amount:N2}",
                run.ReversalRunNumber, run.TotalAdjustments, run.EraEnvelopeIds.Count, run.TotalReversalAmount);

            return await _reversalRunRepository.UpdateAsync(run);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing reversal run {ReversalRunId}", SanitizeForLog(reversalRunId));

            run.Status = ReversalRunStatus.Failed;
            run.Errors.Add($"Execution failed: {ex.Message}");
            run.ExecutionCompletedAt = DateTime.UtcNow;
            run.ExecutionDurationSeconds = run.ExecutionStartedAt.HasValue
                ? (run.ExecutionCompletedAt.Value - run.ExecutionStartedAt.Value).TotalSeconds
                : 0;
            await _reversalRunRepository.UpdateAsync(run);
            throw;
        }
    }

    /// <summary>
    /// A reversal 835 that nets below zero carries its balance forward (BPR02 =
    /// 0, PLB FB). Each provider's own negative net (one PLB FB per payee NPI,
    /// <see cref="EraEnvelope.ForwardBalanceByProvider"/>) opens that provider's
    /// receivable; one provider is never charged another's debt. A balance that
    /// cannot be attributed is recorded on the run only, with a warning. The 835
    /// is already persisted, so a ledger failure never fails the run: it is
    /// reported as an unrecorded receivable.
    /// </summary>
    private async Task RecordForwardBalancesAsync(
        EraEnvelope env, string envelopeId, List<EraPaymentInput> eraInputs, ReversalRun run, string approver)
    {
        var owedTotal = -env.ForwardBalanceAmount;
        var trace = eraInputs.First(i => i.TradingPartnerId == env.TradingPartnerId).Payment.CheckNumber;
        var byProvider = env.ForwardBalanceByProvider ?? new Dictionary<string, decimal>();

        if (byProvider.Count == 0)
        {
            run.OutstandingReceivables.Add(new ProviderReceivable
            {
                TradingPartnerId = env.TradingPartnerId,
                EraEnvelopeId = envelopeId,
                Reference = trace,
                Amount = owedTotal,
            });
            run.OutstandingReceivableAmount += owedTotal;
            run.Warnings.Add(
                $"Trading partner {env.TradingPartnerId}: reversal 835 nets to -{owedTotal:F2}; BPR02 = 0.00 and {owedTotal:F2} is carried forward (PLB FB), " +
                "but it cannot be attributed to one provider (the 835 spans several payee NPIs with mixed balances, or a payment has no NPI), " +
                "so no provider receivable was opened; it needs a person");
            return;
        }

        foreach (var (npi, net) in byProvider.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var owed = -net;
            var receivable = new ProviderReceivable
            {
                TradingPartnerId = env.TradingPartnerId,
                EraEnvelopeId = envelopeId,
                Reference = trace,
                Amount = owed,
                ProviderNpi = npi,
            };
            run.OutstandingReceivables.Add(receivable);
            run.OutstandingReceivableAmount += owed;

            if (_receivables == null)
            {
                run.Warnings.Add(
                    $"Trading partner {env.TradingPartnerId}: NPI {npi} owes {owed:F2}, carried forward (PLB FB) in reversal 835 {envelopeId}; " +
                    "no receivable ledger is configured, so nothing recovers it automatically");
                continue;
            }

            try
            {
                var ledger = await _receivables.RecordForwardBalanceAsync(
                    run.TenantId, npi, env.TradingPartnerId, run, envelopeId, trace, owed, approver);
                receivable.ReceivableId = ledger.Id;
                run.Warnings.Add(
                    $"Trading partner {env.TradingPartnerId}: NPI {npi} owes {owed:F2}, carried forward (PLB FB) in reversal 835 {envelopeId} " +
                    $"as provider receivable {ledger.Id}; later payment runs recover it");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Provider receivable for NPI {Npi} ({Amount:F2}, reversal 835 {EnvelopeId}) could not be recorded in run {ReversalRunNumber}",
                    SanitizeForLog(npi), owed, envelopeId, run.ReversalRunNumber);
                run.Warnings.Add(
                    $"Trading partner {env.TradingPartnerId}: NPI {npi} owes {owed:F2}, carried forward (PLB FB) in reversal 835 {envelopeId}, " +
                    $"but the receivable could not be recorded ({ex.GetType().Name}); it is UNRECORDED in the ledger and needs a person");
            }
        }
    }

    public async Task<ReversalRun> RetryVoidsAsync(string reversalRunId)
    {
        var run = await _reversalRunRepository.GetByIdAsync(reversalRunId);
        if (run == null)
            throw new InvalidOperationException($"Reversal run {reversalRunId} not found");

        if (run.Status is not (ReversalRunStatus.Completed or ReversalRunStatus.Failed))
            throw new InvalidOperationException(
                $"Reversal run {reversalRunId} has not been executed; only an executed run's voids can be retried");

        if (string.IsNullOrWhiteSpace(run.ExecutedBy))
            throw new InvalidOperationException(
                $"Reversal run {reversalRunId} records no approver; its voids cannot be retried");

        using var grant = RunExecutionGrant.Open(run.TenantId, run.Id, run.ExecutedBy);

        var voided = 0;
        var stillPending = new List<string>();
        foreach (var paymentId in run.PaymentIds)
        {
            var payment = await _paymentRepository.GetByIdAsync(paymentId);
            if (payment == null || !payment.IsReversal)
                continue;

            var attempted = false;
            foreach (var cp in payment.ClaimPayments.Where(cp => cp.FinalizedAt == null))
            {
                attempted = true;
                if (await TryVoidAsync(cp, run, adjustmentId: null))
                    voided++;
                else
                    stillPending.Add(cp.ClaimId);
            }
            if (attempted)
                await SavePaymentStatusAsync(payment);
        }

        run.PendingVoidClaimIds = stillPending.Distinct(StringComparer.Ordinal).ToList();
        run.Warnings.Add(
            $"Voids retried by {_actor.UserId} at {DateTime.UtcNow:O}: {voided} claim(s) voided, " +
            $"{run.PendingVoidClaimIds.Count} still pending; no reversal payment was created");
        return await _reversalRunRepository.UpdateAsync(run);
    }

    public async Task<ReversalRun> GetReversalRunAsync(string reversalRunId)
    {
        var run = await _reversalRunRepository.GetByIdAsync(reversalRunId);
        if (run == null)
            throw new InvalidOperationException($"Reversal run {reversalRunId} not found");
        return run;
    }

    public async Task<IEnumerable<ReversalRun>> GetReversalRunsAsync(DateTime? from = null, DateTime? to = null)
    {
        return await _reversalRunRepository.SearchAsync(
            from ?? DateTime.UtcNow.AddMonths(-3),
            to ?? DateTime.UtcNow);
    }

    public async Task CancelReversalRunAsync(string reversalRunId)
    {
        var run = await _reversalRunRepository.GetByIdAsync(reversalRunId);
        if (run == null)
            throw new InvalidOperationException($"Reversal run {reversalRunId} not found");

        if (run.Status == ReversalRunStatus.Running)
            throw new InvalidOperationException("Cannot cancel a running reversal run");

        run.Status = ReversalRunStatus.Cancelled;
        run.CancelledBy = _actor.UserId;
        run.CancelledAt = DateTime.UtcNow;
        await _reversalRunRepository.UpdateAsync(run);
    }

    // ── Private helpers ────────────────────────────────────────────────

    private async Task<List<ClaimAdjustmentDto>> FetchPendingReversalAdjustmentsAsync(string tenantId, ReversalRunCriteria criteria)
    {
        // Explicit-override path — operator hand-curated batch.
        if (criteria.AdjustmentIds is { Count: > 0 } explicitIds)
        {
            var explicitMatches = new List<ClaimAdjustmentDto>();
            foreach (var id in explicitIds)
            {
                var single = await FetchAdjustmentAsync(tenantId, id);
                if (single != null && single.Status == ClaimAdjustmentDtoStatus.PendingReversal)
                    explicitMatches.Add(single);
            }
            return explicitMatches;
        }

        // Filter path — page through the claims-service surface. PageSize
        // matches the 5.12a controller cap (200); we iterate pages until
        // we've collected everything matching the filters so a batch with
        // >200 PendingReversal adjustments doesn't silently drop the
        // remainder. Hard cap at MaxPagesPerRun pages (= 50,000 adjustments)
        // as a runaway-pagination guard; runs hitting the cap surface a
        // warning and the operator re-runs to catch the rest.
        const int pageSize = 200;
        const int maxPagesPerRun = 250;

        var collected = new List<ClaimAdjustmentDto>();
        for (var pageNumber = 1; pageNumber <= maxPagesPerRun; pageNumber++)
        {
            var query = new List<string>
            {
                "status=PendingReversal",
                $"page={pageNumber}",
                $"pageSize={pageSize}",
            };
            if (criteria.AdjustmentDateFrom.HasValue)
                query.Add($"createdFrom={criteria.AdjustmentDateFrom.Value:O}");
            if (criteria.AdjustmentDateTo.HasValue)
                query.Add($"createdTo={criteria.AdjustmentDateTo.Value:O}");

            var response = await _claimsService.ListAdjustmentsAsync(tenantId, string.Join("&", query));
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"claims-service GET /api/v1/adjustments returned {response.StatusCode}");

            var page = await response.Content.ReadFromJsonAsync<ClaimAdjustmentListResponseDto>()
                ?? new ClaimAdjustmentListResponseDto();
            var items = page.Items ?? new List<ClaimAdjustmentDto>();
            if (items.Count == 0) break;

            collected.AddRange(items);

            // Last page either when the response is short or when we've
            // reached the reported total. Total is the canonical signal
            // (Items.Count == pageSize on a non-final page is possible);
            // fall back to count-based termination when Total is unset.
            if (page.Total > 0 && collected.Count >= page.Total) break;
            if (items.Count < pageSize) break;
        }

        return collected;
    }

    private async Task<ClaimAdjustmentDto?> FetchAdjustmentAsync(string tenantId, string adjustmentId)
    {
        var response = await _claimsService.GetAdjustmentAsync(tenantId, adjustmentId);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"claims-service GET /api/v1/adjustments/{adjustmentId} returned {response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<ClaimAdjustmentDto>();
    }

    private async Task<ClaimDto?> FetchClaimAsync(string tenantId, string claimId)
    {
        var response = await _claimsService.GetClaimAsync(tenantId, claimId);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"claims-service GET /api/claims/{claimId} returned {response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<ClaimDto>();
    }

    private async Task<Dictionary<string, TradingPartnerSummary>> ResolveTradingPartnersAsync(
        IEnumerable<string> npis, string tenantId, string environment, List<string> warnings)
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
                OriginatingCompanyId = _configuration["Era:OriginatingCompanyId"],
                OriginatingCompanySupplementalCode = _configuration["Era:OriginatingCompanySupplementalCode"],
                PayeeRoutingNumber = _configuration["Era:PayeeRoutingNumber"],
                PayeeAccountNumber = _configuration["Era:PayeeAccountNumber"],
            };
        }
        return seen;
    }

    /// <summary>
    /// The claim payment payment-service recorded for <paramref name="claimId"/>
    /// in its (non-reversal) payment, or null with the reason when there is
    /// none, or more than one so the amount paid is ambiguous.
    /// </summary>
    private async Task<(ClaimPayment? Original, string? Reason)> FindOriginalClaimPaymentAsync(string claimId)
    {
        var payments = await _paymentRepository.GetByClaimIdAsync(claimId) ?? Enumerable.Empty<Payment>();
        var recorded = payments
            .Where(p => !p.IsReversal)
            .SelectMany(p => p.ClaimPayments.Where(cp => cp.ClaimId == claimId))
            .ToList();
        return recorded.Count switch
        {
            1 => (recorded[0], null),
            0 => (null, "payment-service holds no recorded payment for it, so the amount paid is unknown"),
            _ => (null, $"payment-service holds {recorded.Count} recorded payments for it, so the amount paid is ambiguous"),
        };
    }

    /// <summary>
    /// The reversal payment: <paramref name="reversalClaim"/> (the original
    /// claim payment negated, CLP02 = 22, from
    /// <see cref="Era835ClaimPaymentBuilder.BuildReversal"/>), recouping what
    /// the original actually paid.
    /// </summary>
    private async Task<Payment> BuildReversalPaymentAsync(
        ClaimDto pred,
        ClaimPayment reversalClaim,
        ReversalRun run,
        string? tradingPartnerId,
        string checkNumber,
        string approver)
    {
        var providerNpi = pred.PayeeNpi;
        var payment = new Payment
        {
            CheckNumber = checkNumber,
            PaymentMethod = ReversalPaymentMethod,
            // The negated recorded payment (CLP04 of the reversal).
            TotalPaymentAmount = reversalClaim.PaymentAmount,
            PaymentDate = run.ExecutionStartedAt ?? DateTime.UtcNow,
            PayerName = _configuration["Payer:Name"] ?? "Cloud Health Office",
            PayerId = _configuration["Payer:Id"] ?? "CHO",
            PayeeName = pred.PayeeNameOr(string.IsNullOrWhiteSpace(providerNpi) ? "Provider" : providerNpi),
            PayeeNPI = providerNpi,
            // The predecessor's 837 pay-to address (2010AB), as on its payment.
            PayeeAddress = pred.PayToAddress,
            TradingPartnerId = tradingPartnerId,
            // Recouped, not yet voided in claims-service; Posted once voided.
            Status = PaymentStatus.PaidPendingFinalize,
            PostedBy = approver,
            PostedAt = DateTime.UtcNow,
            RunId = run.Id,
            RunNumber = run.ReversalRunNumber,
            IsReversal = true,
            ClaimPayments = new List<ClaimPayment> { reversalClaim },
        };
        return await _paymentRepository.CreateAsync(payment);
    }

    private async Task VoidPredecessorsAsync(
        List<(ClaimAdjustmentDto Adjustment, ClaimDto Predecessor, Payment Payment)> adjustmentsToVoid,
        ReversalRun run,
        IReadOnlyDictionary<string, string> claimToEnvelopeId)
    {
        foreach (var (adj, pred, payment) in adjustmentsToVoid)
        {
            // Pull the envelope id we persisted for this claim so warning
            // messages and structured logs let operators trace which
            // reversal envelope contained the claim being voided.
            var envelopeId = claimToEnvelopeId.TryGetValue(pred.Id, out var envId) ? envId : "<none>";
            var cp = payment.ClaimPayments.First(c => c.ClaimId == pred.Id);
            if (!await TryVoidAsync(cp, run, adj.Id))
            {
                run.Warnings.Add(
                    $"Void of predecessor {pred.Id} for adjustment {adj.Id} (envelope {envelopeId}) failed: {cp.FinalizeError}");
                run.Errors.Add(
                    $"Predecessor {pred.Id} recouped by {payment.CheckNumber} but claims-service did not void it ({cp.FinalizeError}); " +
                    $"reversal payment is PaidPendingFinalize; retry with POST /api/reversalruns/{run.Id}/void");
                run.PendingVoidClaimIds.Add(pred.Id);
            }
            await SavePaymentStatusAsync(payment);
        }
    }

    /// <summary>
    /// A later reversal run selected an adjustment whose predecessor an earlier
    /// run already recouped. If that reversal's void is pending and its 835 was
    /// emitted, void now (idempotent in claims-service); never a new reversal.
    /// </summary>
    private async Task RetryPendingVoidForClaimAsync(string claimId, ClaimAdjustmentDto adj, ReversalRun run)
    {
        var payments = await _paymentRepository.GetByClaimIdAsync(claimId) ?? Enumerable.Empty<Payment>();
        foreach (var payment in payments.Where(p => p.IsReversal && !string.IsNullOrEmpty(p.EraEnvelopeId)))
        {
            var cp = payment.ClaimPayments.FirstOrDefault(c => c.ClaimId == claimId && c.FinalizedAt == null);
            if (cp == null)
                continue;

            var ok = await TryVoidAsync(cp, run, adj.Id, payment.RunId);
            await SavePaymentStatusAsync(payment);
            run.Warnings.Add(ok
                ? $"Predecessor {claimId}: pending void from reversal run {payment.RunNumber} completed"
                : $"Predecessor {claimId}: pending void from reversal run {payment.RunNumber} failed again: {cp.FinalizeError}");

            if (ok && !string.IsNullOrEmpty(payment.RunId) && payment.RunId != run.Id)
            {
                var original = await _reversalRunRepository.GetByIdAsync(payment.RunId);
                if (original != null && original.PendingVoidClaimIds.Remove(claimId))
                {
                    original.Warnings.Add($"Predecessor {claimId} voided by reversal run {run.ReversalRunNumber} at {DateTime.UtcNow:O}");
                    await _reversalRunRepository.UpdateAsync(original);
                }
            }
        }
    }

    /// <summary>
    /// One POST /api/claims/{id}/void. claims-service has no actor field it
    /// would trust from a caller; the approver goes into the void reason (its
    /// audit trail) and the call itself carries payment-service's service token.
    /// </summary>
    private async Task<bool> TryVoidAsync(ClaimPayment cp, ReversalRun run, string? adjustmentId, string? reversalRunId = null)
    {
        try
        {
            var reason = adjustmentId != null
                ? $"Reversed by ReversalRun {run.ReversalRunNumber} (adjustment {adjustmentId}); released by {run.ExecutedBy}"
                : $"Reversed by ReversalRun {run.ReversalRunNumber}; released by {run.ExecutedBy}";
            var body = new ClaimVoidPostBody
            {
                Reason = reason,
                ReversalRunId = reversalRunId ?? run.Id,
            };
            var tenantId = RunExecutionGrant.Current?.TenantId ?? run.TenantId;
            using var response = await _claimsService.VoidClaimAsync(tenantId, cp.ClaimId, body);
            if (response.IsSuccessStatusCode)
            {
                cp.FinalizedAt = DateTime.UtcNow;
                cp.FinalizeError = null;
                if (adjustmentId != null)
                    run.AdjustmentIds.Add(adjustmentId);
                return true;
            }

            var bodyText = await response.Content.ReadAsStringAsync();
            cp.FinalizeError = $"claims-service returned {(int)response.StatusCode}";
            _logger.LogWarning(
                "Void of predecessor {ClaimId} (adjustment {AdjustmentId}) returned {Status}: {Body}",
                SanitizeForLog(cp.ClaimId), SanitizeForLog(adjustmentId), response.StatusCode, SanitizeForLog(bodyText));
            return false;
        }
        catch (Exception ex)
        {
            cp.FinalizeError = $"void call threw: {ex.Message}";
            _logger.LogError(ex, "Void of predecessor {ClaimId} (adjustment {AdjustmentId}) threw",
                SanitizeForLog(cp.ClaimId), SanitizeForLog(adjustmentId));
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

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// ── Cross-service DTOs (mirror claims-service surfaces) ─────────────────

/// <summary>
/// Mirror of claims-service <c>ClaimAdjustmentResponse</c> — the
/// payload returned by <c>GET /api/v1/adjustments</c>. Field shapes are
/// kept narrow to what 5.12b consumes; additional fields surfaced by
/// claims-service deserialize into ignored properties.
/// </summary>
public class ClaimAdjustmentDto
{
    public string Id { get; set; } = string.Empty;
    public string ClaimVersionId { get; set; } = string.Empty;
    public string PredecessorClaimId { get; set; } = string.Empty;
    public string PredecessorVersionId { get; set; } = string.Empty;
    public string NewClaimId { get; set; } = string.Empty;
    public string AdjustmentReason { get; set; } = string.Empty;
    public ClaimAdjustmentDtoStatus Status { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ReversalRunId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClaimAdjustmentDtoStatus
{
    AwaitingReadjudication = 1,
    PendingReversal = 2,
    Active = 3,
    Failed = 4,
}

public class ClaimAdjustmentListResponseDto
{
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<ClaimAdjustmentDto>? Items { get; set; }
}

internal class ClaimVoidPostBody
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
    [JsonPropertyName("reversalRunId")]
    public string? ReversalRunId { get; set; }
}
