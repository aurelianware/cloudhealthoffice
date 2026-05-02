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
///   <item><description>Constructs a negative-amount <see cref="Payment"/> sign-flipping the predecessor's payment + CAS data; sets <c>ClaimPayment.ClaimStatusCode = "22"</c>.</description></item>
///   <item><description>Generates one reversal 835 envelope per trading partner via <see cref="IBatchEraGeneratorService"/>; persists with <see cref="EraEnvelopeRecord.ReversalRunId"/> set.</description></item>
///   <item><description>Calls <c>POST /api/claims/{id}/void</c> on claims-service; the claims-service hook transitions the originating adjustment <c>PendingReversal → Active</c> on success.</description></item>
/// </list>
/// </summary>
public interface IReversalRunService
{
    Task<ReversalRun> CreateReversalRunAsync(ReversalRunCriteria criteria, string? createdBy = null, string? description = null);
    Task<ReversalRun> ExecuteReversalRunAsync(string reversalRunId);
    Task<ReversalRun> GetReversalRunAsync(string reversalRunId);
    Task<IEnumerable<ReversalRun>> GetReversalRunsAsync(DateTime? from = null, DateTime? to = null);
    Task CancelReversalRunAsync(string reversalRunId);
}

public class ReversalRunService : IReversalRunService
{
    private readonly IPaymentRepository _paymentRepository;
    private readonly IReversalRunRepository _reversalRunRepository;
    private readonly IBatchEraGeneratorService _batchEraGenerator;
    private readonly IEraEnvelopeRepository _envelopeRepository;
    private readonly ITradingPartnersClient _tradingPartnersClient;
    private readonly HttpClient _claimsServiceClient;
    private readonly ILogger<ReversalRunService> _logger;
    private readonly IConfiguration _configuration;

    public ReversalRunService(
        IPaymentRepository paymentRepository,
        IReversalRunRepository reversalRunRepository,
        IBatchEraGeneratorService batchEraGenerator,
        IEraEnvelopeRepository envelopeRepository,
        ITradingPartnersClient tradingPartnersClient,
        IHttpClientFactory httpClientFactory,
        ILogger<ReversalRunService> logger,
        IConfiguration configuration)
    {
        _paymentRepository = paymentRepository;
        _reversalRunRepository = reversalRunRepository;
        _batchEraGenerator = batchEraGenerator;
        _envelopeRepository = envelopeRepository;
        _tradingPartnersClient = tradingPartnersClient;
        _claimsServiceClient = httpClientFactory.CreateClient("ClaimsService");
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
            throw new InvalidOperationException($"Reversal run {reversalRunId} is not in Pending status");

        run.Status = ReversalRunStatus.Running;
        run.ExecutionStartedAt = DateTime.UtcNow;
        await _reversalRunRepository.UpdateAsync(run);

        try
        {
            // Step 1 — fetch the PendingReversal adjustment batch from
            //          claims-service. The 5.12a list endpoint already
            //          supports the filter shape we need (status +
            //          createdBy + date range + pagination).
            var adjustments = await FetchPendingReversalAdjustmentsAsync(run.Criteria);

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
                var pred = await FetchClaimAsync(adj.PredecessorClaimId);
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
                        && string.Equals(p.PayToProviderNPI ?? p.BillingProviderNPI, npi, StringComparison.Ordinal))
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
                    .Select(c => c.PayToProviderNPI ?? c.BillingProviderNPI)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.Ordinal),
                run.TenantId,
                environment,
                run.Warnings);

            // Step 4 — construct one reversal Payment per adjustment;
            //          group by trading partner for envelope generation.
            var eraInputs = new List<EraPaymentInput>();
            var adjustmentsToVoid = new List<(ClaimAdjustmentDto Adjustment, ClaimDto Predecessor)>();
            foreach (var adj in adjustments)
            {
                if (!predecessors.TryGetValue(adj.PredecessorClaimId, out var pred))
                    continue;

                var providerNpi = pred.PayToProviderNPI ?? pred.BillingProviderNPI;
                string? tradingPartnerId = null;
                if (!string.IsNullOrEmpty(providerNpi)
                    && resolvedTradingPartners.TryGetValue(providerNpi, out var partner))
                {
                    tradingPartnerId = partner.TradingPartnerId;
                }

                var checkNumber = $"R-{Guid.NewGuid().ToString("N").Substring(0, 8).ToUpper()}";
                var payment = await BuildReversalPaymentAsync(pred, run, tradingPartnerId, checkNumber);

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
                    adjustmentsToVoid.Add((adj, pred));
                }
                else
                {
                    run.Warnings.Add(
                        $"Reversal payment {payment.CheckNumber} for adjustment {adj.Id} skipped from envelope — no trading partner resolved");
                }
            }

            run.TotalAdjustments = adjustments.Count;

            // Step 5 — batched 835 reversal generation. CLP02="22" was set
            //          upstream when constructing the Payment; CAS amounts
            //          are sign-flipped on each ClaimPayment.
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
                    ControlNumber = env.ControlNumber,
                    ClaimIds = env.ClaimIds.ToList(),
                });
                run.EraEnvelopeIds.Add(record.Id);
                foreach (var claimId in env.ClaimIds)
                {
                    claimToEnvelopeId[claimId] = record.Id;
                }
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
        await _reversalRunRepository.UpdateAsync(run);
    }

    // ── Private helpers ────────────────────────────────────────────────

    private async Task<List<ClaimAdjustmentDto>> FetchPendingReversalAdjustmentsAsync(ReversalRunCriteria criteria)
    {
        // Explicit-override path — operator hand-curated batch.
        if (criteria.AdjustmentIds is { Count: > 0 } explicitIds)
        {
            var explicitMatches = new List<ClaimAdjustmentDto>();
            foreach (var id in explicitIds)
            {
                var single = await FetchAdjustmentAsync(id);
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

            var url = "/api/v1/adjustments?" + string.Join("&", query);
            var response = await _claimsServiceClient.GetAsync(url);
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

    private async Task<ClaimAdjustmentDto?> FetchAdjustmentAsync(string adjustmentId)
    {
        var response = await _claimsServiceClient.GetAsync($"/api/v1/adjustments/{adjustmentId}");
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"claims-service GET /api/v1/adjustments/{adjustmentId} returned {response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<ClaimAdjustmentDto>();
    }

    private async Task<ClaimDto?> FetchClaimAsync(string claimId)
    {
        var response = await _claimsServiceClient.GetAsync($"/api/claims/{claimId}");
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
                PayeeRoutingNumber = _configuration["Era:PayeeRoutingNumber"],
                PayeeAccountNumber = _configuration["Era:PayeeAccountNumber"],
            };
        }
        return seen;
    }

    private async Task<Payment> BuildReversalPaymentAsync(
        ClaimDto pred,
        ReversalRun run,
        string? tradingPartnerId,
        string checkNumber)
    {
        var providerNpi = pred.PayToProviderNPI ?? pred.BillingProviderNPI;
        var originalApproved = pred.ApprovedAmount ?? pred.TotalChargeAmount;

        // Mirror the original CAS data (sign-flipped). We don't have the
        // payment-service's Payment row from the original PaymentRun in
        // hand — we work from the claim's AdjudicationResult, which is the
        // source of truth and is what the original payment was built from.
        var headerCas = (pred.AdjudicationResult?.AdjustmentReasons ?? new List<ClaimAdjustmentReasonDto>())
            .Select(r => new ClaimAdjustment
            {
                GroupCode = r.GroupCode,
                ReasonCode = r.ReasonCode,
                Amount = -r.Amount,
                ReasonDescription = r.Description,
            })
            .ToList();

        var payment = new Payment
        {
            CheckNumber = checkNumber,
            PaymentMethod = "ACH",
            TotalPaymentAmount = -originalApproved,
            PaymentDate = run.ExecutionStartedAt ?? DateTime.UtcNow,
            PayerName = _configuration["Payer:Name"] ?? "Cloud Health Office",
            PayerId = _configuration["Payer:Id"] ?? "CHO",
            PayeeName = pred.ProviderName ?? providerNpi ?? "Provider",
            PayeeNPI = providerNpi,
            TradingPartnerId = tradingPartnerId,
            Status = PaymentStatus.Posted,
            IsReversal = true,
            ClaimPayments = new List<ClaimPayment>
            {
                new ClaimPayment
                {
                    ClaimId = pred.Id,
                    PatientControlNumber = pred.ClaimNumber,
                    // CLP02 "22" — Reversal of Previous Payment (X12
                    // 005010X221A1). Set upstream of BatchEraGeneratorService
                    // per Premise C; the generator emits whatever's in the
                    // ClaimPayment.
                    ClaimStatusCode = "22",
                    ChargeAmount = pred.TotalChargeAmount,
                    PaymentAmount = -originalApproved,
                    PatientResponsibilityAmount = -(pred.PatientResponsibility ?? 0),
                    PayerClaimControlNumber = pred.PayerClaimControlNumber,
                    MemberId = pred.MemberId,
                    RenderingProviderNPI = pred.RenderingProviderNPI,
                    ClaimAdjustments = headerCas,
                    ServiceLines = (pred.ServiceLines ?? new List<ClaimServiceLineDto>())
                        .Select(sl => new ServiceLinePayment
                        {
                            LineNumber = sl.LineNumber,
                            ProcedureCode = sl.ProcedureCode,
                            ChargeAmount = sl.ChargeAmount,
                            PaymentAmount = -(sl.PaidAmount ?? sl.ChargeAmount),
                            RevenueCode = sl.RevenueCode,
                            Units = sl.Units,
                            ServiceDateFrom = sl.ServiceDateFrom,
                            ServiceDateTo = sl.ServiceDateTo,
                            // No per-line CAS data on the claims-service
                            // ClaimDto today; reversal envelopes carry the
                            // header-level sign-flipped CAS only. Phase 2
                            // surfaces line CAS once claims-service projects
                            // EditFailures into the read DTO.
                            Adjustments = new List<ServiceLineAdjustment>(),
                        })
                        .ToList(),
                },
            },
        };
        return await _paymentRepository.CreateAsync(payment);
    }

    private async Task VoidPredecessorsAsync(
        List<(ClaimAdjustmentDto Adjustment, ClaimDto Predecessor)> adjustmentsToVoid,
        ReversalRun run,
        IReadOnlyDictionary<string, string> claimToEnvelopeId)
    {
        foreach (var (adj, pred) in adjustmentsToVoid)
        {
            // Pull the envelope id we persisted for this claim so warning
            // messages and structured logs let operators trace which
            // reversal envelope contained the claim being voided. The map
            // is keyed by ClaimId; absence (rare — claim filtered out of
            // envelope emission upstream) falls back to "<none>".
            var envelopeId = claimToEnvelopeId.TryGetValue(pred.Id, out var envId) ? envId : "<none>";
            try
            {
                var body = new ClaimVoidPostBody
                {
                    Reason = $"Reversed by ReversalRun {run.ReversalRunNumber} (adjustment {adj.Id})",
                    ReversalRunId = run.Id,
                };
                var response = await _claimsServiceClient.PostAsJsonAsync(
                    $"/api/claims/{pred.Id}/void", body);
                if (response.IsSuccessStatusCode)
                {
                    run.AdjustmentIds.Add(adj.Id);
                }
                else
                {
                    var bodyText = await response.Content.ReadAsStringAsync();
                    run.Warnings.Add(
                        $"Void of predecessor {pred.Id} for adjustment {adj.Id} (envelope {envelopeId}) returned {(int)response.StatusCode}");
                    _logger.LogWarning(
                        "Void of predecessor {ClaimId} for adjustment {AdjustmentId} (envelope {EnvelopeId}) returned {Status}: {Body}",
                        SanitizeForLog(pred.Id), SanitizeForLog(adj.Id), SanitizeForLog(envelopeId),
                        response.StatusCode, SanitizeForLog(bodyText));
                }
            }
            catch (Exception ex)
            {
                run.Warnings.Add(
                    $"Void of predecessor {pred.Id} for adjustment {adj.Id} (envelope {envelopeId}) threw: {ex.Message}");
                _logger.LogError(ex,
                    "Void of predecessor {ClaimId} for adjustment {AdjustmentId} (envelope {EnvelopeId}) threw",
                    SanitizeForLog(pred.Id), SanitizeForLog(adj.Id), SanitizeForLog(envelopeId));
            }
        }
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
