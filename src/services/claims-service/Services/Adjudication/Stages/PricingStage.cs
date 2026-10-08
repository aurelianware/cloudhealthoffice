using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace ClaimsService.Services.Adjudication.Stages;

/// <summary>
/// Prices every claim line against the provider's contracted (or the plan's
/// default) fee schedule before benefits are applied, and records the
/// per-line allowed amounts on <see cref="ClaimAdjudicationContext.PricingResult"/>
/// for <see cref="BenefitCalculationStage"/> (Order=300).
///
/// <para>
/// <b>Why this stage exists.</b> The async pipeline had no pricing step:
/// <see cref="BenefitCalculationStage"/> sent an empty <c>AllowedAmounts</c>
/// map, so the benefit engine silently fell back to allowed = billed and
/// paid billed charges. The synchronous <c>AdjudicationController.Adjudicate</c>
/// path in benefit-plan-service always priced via
/// <c>IRateResolutionService.ResolveBatchAsync</c>; this stage reaches the
/// same engine through the side-effect-free <c>resolve-rates</c> endpoint
/// (<see cref="IFeeSchedulePricingClient"/>), so both paths share one
/// pricing contract.
/// </para>
///
/// <para>
/// <b>Fail closed.</b> A line is unpriced when the engine fell back to
/// billed charges (<see cref="RateSource.BilledCharges"/> — no contract,
/// fee schedule, or rate line matched), when no result came back for the
/// line, or when the pricing call itself failed. Any unpriced line pends
/// the claim (<c>PendCode=NOCONTRACT</c>, or <c>PRICING</c> when the
/// service was unreachable) and <see cref="BenefitCalculationStage"/>
/// refuses to calculate — no accumulator write, no billed-charge payment.
/// Like <see cref="ProviderIntegrityStage"/>, there is no fail-open mode:
/// allowed = billed is never a legitimate degraded posture. Zero-charge
/// lines that find no rate are the one exception — allowed $0 cannot
/// overpay, so they are recorded as priced at $0.
/// </para>
///
/// <para>
/// Order 250: after <see cref="NetworkCredentialingStage"/> (200) so the
/// pipeline has already applied network enforcement, before
/// <see cref="BenefitCalculationStage"/> (300) which consumes the result.
/// </para>
/// </summary>
public sealed class PricingStage : IClaimAdjudicationStage
{
    public const string StageName = "Pricing";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> for lines with no contracted or
    /// plan-default fee schedule rate. Existing work-queue vocabulary
    /// ("Provider Not Contracted" bucket).
    /// </summary>
    public const string NoContractPendCode = "NOCONTRACT";

    /// <summary>
    /// <see cref="PendDetails.PendCode"/> when the fee-schedule resolution
    /// service could not be reached or returned an unusable response.
    /// </summary>
    public const string PricingUnavailablePendCode = "PRICING";

    private readonly IFeeSchedulePricingClient _pricingClient;
    private readonly ILogger<PricingStage> _logger;

    public PricingStage(
        IFeeSchedulePricingClient pricingClient,
        ILogger<PricingStage> logger)
    {
        _pricingClient = pricingClient;
        _logger = logger;
    }

    public string Name => StageName;
    public int Order => 250;
    public bool IsRequired => false;

    public async Task<ClaimAdjudicationStageResult> ExecuteAsync(
        ClaimAdjudicationContext context,
        CancellationToken ct)
    {
        var claim = context.Claim;

        if (claim.ClaimLines.Count == 0)
        {
            // Nothing to price. ScrubbingStage owns rejecting line-less claims.
            context.PricingResult = new PricingOutcome();
            return ClaimAdjudicationStageResult.Pass(StageName);
        }

        var duplicateLineNumbers = claim.ClaimLines
            .GroupBy(l => l.LineNumber)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateLineNumbers.Count > 0)
        {
            // Allowed amounts are keyed by line number on both the pricing
            // response and the benefit request; duplicates make the mapping
            // ambiguous, so no line can be trusted.
            var unpriced = claim.ClaimLines
                .Select(l => new UnpricedLine(
                    l.LineNumber, l.ProcedureCode,
                    $"duplicate line number {l.LineNumber} on claim; per-line pricing is ambiguous"))
                .ToList();
            context.PricingResult = new PricingOutcome { UnpricedLines = unpriced };
            return Pend(context, NoContractPendCode,
                $"Claim has duplicate line numbers ({string.Join(", ", duplicateLineNumbers)}); lines cannot be priced.");
        }

        var requests = BuildRequests(context);

        PricingResultSet? priced;
        try
        {
            priced = await _pricingClient
                .ResolveBatchAsync(context.TenantId, requests, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Fee schedule pricing threw for claim {ClaimVersionId}",
                SanitizeForLog(context.ClaimVersionId));
            priced = null;
        }

        if (priced is null)
        {
            _logger.LogWarning(
                "Fee schedule pricing unavailable for claim {ClaimVersionId}; pending",
                SanitizeForLog(context.ClaimVersionId));
            context.PricingResult = new PricingOutcome { PricingUnavailable = true };
            return Pend(context, PricingUnavailablePendCode,
                "Fee schedule pricing service unavailable; claim lines could not be priced.");
        }

        var outcome = Evaluate(claim, requests, priced);
        context.PricingResult = outcome;

        if (!outcome.IsFullyPriced)
        {
            var detail = string.Join("; ", outcome.UnpricedLines
                .Select(u => $"line {u.LineNumber} ({u.ProcedureCode}): {u.Reason}"));
            return Pend(context, NoContractPendCode,
                $"{outcome.UnpricedLines.Count} of {claim.ClaimLines.Count} claim line(s) could not be priced: {detail}");
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    internal static List<PricingRequest> BuildRequests(ClaimAdjudicationContext context)
    {
        var claim = context.Claim;

        // Rendering provider preferred (PricingRequest.ProviderNpi contract),
        // billing provider otherwise.
        var providerNpi = !string.IsNullOrWhiteSpace(claim.RenderingProviderNPI)
            ? claim.RenderingProviderNPI!
            : claim.BillingProviderNPI;

        // Same plan identity the benefit stage uses: the resolved Guid when
        // benefit-plan-service returned one, the claim's raw id otherwise.
        var planId = context.ResolvedPlan?.PlanGuid?.ToString()
            ?? claim.BenefitPlanId
            ?? string.Empty;

        var totalLines = claim.ClaimLines.Count;

        return claim.ClaimLines
            .OrderBy(l => l.LineNumber)
            .Select(line => new PricingRequest
            {
                TenantId = context.TenantId,
                ProcedureCode = line.ProcedureCode,
                Modifiers = line.Modifiers.ToList(),
                ProviderNpi = providerNpi,
                PlaceOfServiceCode = !string.IsNullOrEmpty(line.PlaceOfServiceCode)
                    ? line.PlaceOfServiceCode
                    : claim.PlaceOfServiceCode,
                ServiceDate = line.ServiceDateFrom != default
                    ? line.ServiceDateFrom
                    : claim.ServiceDateFrom,
                PlanId = planId,
                // Matches BenefitCalculationStage's line billed amount so the
                // engine's contractual adjustment (billed − allowed) lines up
                // with what the benefit engine reports as billed.
                BilledAmount = line.ChargeAmount * line.Units,
                Units = line.Units,
                LineNumber = line.LineNumber,
                TotalLineCount = totalLines,
                // DrgCode / LengthOfStay: the claim model carries neither yet;
                // DRG-schedule lines therefore find no rate and pend rather
                // than being guessed.
            })
            .ToList();
    }

    internal static PricingOutcome Evaluate(
        AdapterClaim claim,
        IReadOnlyList<PricingRequest> requests,
        PricingResultSet priced)
    {
        var resultsByLine = new Dictionary<int, PricingResult>();
        foreach (var r in priced.LineResults)
        {
            resultsByLine.TryAdd(r.LineNumber, r);
        }

        var allowed = new Dictionary<int, decimal>();
        var unpriced = new List<UnpricedLine>();

        foreach (var request in requests)
        {
            if (!resultsByLine.TryGetValue(request.LineNumber, out var result))
            {
                unpriced.Add(new UnpricedLine(
                    request.LineNumber, request.ProcedureCode,
                    "no pricing result returned for line"));
                continue;
            }

            if (result.AllowedAmount < 0)
            {
                unpriced.Add(new UnpricedLine(
                    request.LineNumber, request.ProcedureCode,
                    $"pricing returned a negative allowed amount ({result.AllowedAmount})"));
                continue;
            }

            if (result.RateSource == RateSource.BilledCharges)
            {
                if (request.BilledAmount == 0m)
                {
                    // No rate, but nothing billed: $0 allowed cannot overpay.
                    allowed[request.LineNumber] = 0m;
                    continue;
                }

                unpriced.Add(new UnpricedLine(
                    request.LineNumber, request.ProcedureCode,
                    "no contracted or plan-default fee schedule rate (engine fell back to billed charges)"));
                continue;
            }

            allowed[request.LineNumber] = result.AllowedAmount;
        }

        return new PricingOutcome
        {
            AllowedAmounts = allowed,
            UnpricedLines = unpriced,
            RawResult = priced,
        };
    }

    private static ClaimAdjudicationStageResult Pend(
        ClaimAdjudicationContext context,
        string pendCode,
        string reason)
    {
        // Preserve an earlier stage's structured pend (e.g. ProviderIntegrity
        // MEDREVIEW) — the first pend reason is the one the work queue sees.
        context.PendDetails ??= new PendDetails
        {
            PendCode = pendCode,
            PendReason = reason is { Length: > 500 } ? reason[..500] : reason,
            PendedAt = DateTime.UtcNow,
        };
        return ClaimAdjudicationStageResult.Pend(StageName, reason);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");
}
