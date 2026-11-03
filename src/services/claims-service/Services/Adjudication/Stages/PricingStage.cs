using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Repositories;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.Infrastructure.Logging;

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
/// paid billed charges. This stage prices through the fee schedule engine
/// (<c>IRateResolutionService.ResolveBatchAsync</c>) via benefit-plan-service's
/// side-effect-free <c>resolve-rates</c> endpoint
/// (<see cref="IFeeSchedulePricingClient"/>).
/// </para>
///
/// <para>
/// <b>Fail closed.</b> A line is unpriced when the engine fell back to
/// billed charges (<see cref="RateSource.BilledCharges"/> — no contract,
/// fee schedule, or rate line matched), when a matched rate could not be
/// computed (<see cref="RateSource.Unresolved"/>, allowed $0 with an
/// <c>UnresolvedReason</c>), when no result came back for the
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
    private readonly IClaimRepository? _claims;

    /// <param name="claims">
    /// Reads the member's other inpatient claims for a state Medicaid stay,
    /// so a monthly day limit (the adult IMD limit) counts the whole month.
    /// Without it only the claim's own days count.
    /// </param>
    public PricingStage(
        IFeeSchedulePricingClient pricingClient,
        ILogger<PricingStage> logger,
        IClaimRepository? claims = null)
    {
        _pricingClient = pricingClient;
        _logger = logger;
        _claims = claims;
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

        if (_claims is not null && requests.Count > 0 && MedicaidDrgMethods.IsSet(requests[0].MedicaidDrgMethod))
        {
            IReadOnlyList<OtherInpatientDays> others;
            try
            {
                others = await OtherInpatientDaysAsync(context, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Without the member's other stays a monthly day limit could
                // be passed unseen; pend rather than price on half the month.
                _logger.LogError(ex,
                    "Reading the member's other inpatient claims failed for claim {ClaimVersionId}",
                    LogSanitizer.Sanitize(context.ClaimVersionId));
                context.PricingResult = new PricingOutcome { PricingUnavailable = true };
                return Pend(context, PricingUnavailablePendCode,
                    "The member's other inpatient claims could not be read to check the monthly IMD day limit.");
            }
            requests[0] = requests[0] with { OtherInpatientDays = others };
        }

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
                LogSanitizer.Sanitize(context.ClaimVersionId));
            priced = null;
        }

        if (priced is null)
        {
            _logger.LogWarning(
                "Fee schedule pricing unavailable for claim {ClaimVersionId}; pending",
                LogSanitizer.Sanitize(context.ClaimVersionId));
            context.PricingResult = new PricingOutcome { PricingUnavailable = true };
            return Pend(context, PricingUnavailablePendCode,
                "Fee schedule pricing service unavailable; claim lines could not be priced.");
        }

        var outcome = Evaluate(claim, requests, priced);
        context.PricingResult = outcome;
        RecordSupplementalPayments(context, priced);
        RecordLinePricing(context, requests, priced);

        if (!outcome.IsFullyPriced)
        {
            var detail = string.Join("; ", outcome.UnpricedLines
                .Select(u => $"line {u.LineNumber} ({u.ProcedureCode}): {u.Reason}"));
            return Pend(context, NoContractPendCode,
                $"{outcome.UnpricedLines.Count} of {claim.ClaimLines.Count} claim line(s) could not be priced: {detail}");
        }

        return ClaimAdjudicationStageResult.Pass(StageName);
    }

    /// <summary>
    /// Room and board days (revenue codes 0100-0219, leave 0180-0189
    /// excluded; one day per unit from the line's service date) on the
    /// member's other live institutional claims whose service period touches
    /// the calendar months of this stay. This claim's own chain is left out.
    /// </summary>
    internal async Task<IReadOnlyList<OtherInpatientDays>> OtherInpatientDaysAsync(ClaimAdjudicationContext context, CancellationToken ct)
    {
        var claim = context.Claim;
        if (string.IsNullOrWhiteSpace(claim.MemberId)) return [];

        var from = claim.ServiceDateFrom;
        var to = claim.ServiceDateTo != default ? claim.ServiceDateTo : claim.ServiceDateFrom;
        foreach (var line in claim.ClaimLines.Where(l => l.ServiceDateFrom != default))
        {
            if (line.ServiceDateFrom < from) from = line.ServiceDateFrom;
            var last = line.ServiceDateFrom.AddDays(Math.Max(0, (int)Math.Ceiling(line.Units) - 1));
            if (last > to) to = last;
        }
        var monthStart = new DateTime(from.Year, from.Month, 1);
        var monthEnd = new DateTime(to.Year, to.Month, 1).AddMonths(1).AddDays(-1);

        var chainKey = DuplicateClaimStage.ChainKey(claim.ClaimVersionId, claim.Id);
        var priors = await _claims!
            .FindDuplicateCandidatesAsync(context.TenantId, claim.MemberId, monthStart, monthEnd, chainKey, ct)
            .ConfigureAwait(false);

        var result = new List<OtherInpatientDays>();
        foreach (var prior in priors)
        {
            // Same in-memory guard as the duplicate check: never this claim,
            // its chain, its predecessor, or a dead claim.
            if (!string.Equals(prior.TenantId, context.TenantId, StringComparison.Ordinal)) continue;
            if (!string.Equals(prior.MemberId, claim.MemberId, StringComparison.Ordinal)) continue;
            if (string.Equals(prior.Id, claim.Id, StringComparison.Ordinal)) continue;
            if (string.Equals(DuplicateClaimStage.ChainKey(prior.ClaimVersionId, prior.Id), chainKey, StringComparison.Ordinal)) continue;
            if (string.Equals(prior.Id, claim.PredecessorVersionId, StringComparison.Ordinal)) continue;
            if (ClaimStatusSemantics.IsDenied(prior.Status) || prior.Status == ClaimStatus.Voided) continue;
            if (prior.VersionState is ClaimVersionState.Draft or ClaimVersionState.Denied
                or ClaimVersionState.Voided or ClaimVersionState.Adjusted) continue;
            if (prior.SupersededAt is not null || prior.ClaimFrequencyCode?.Trim() == "8") continue;
            if (prior.ClaimType != ClaimsService.Models.ClaimType.Institutional) continue;
            if (string.IsNullOrWhiteSpace(prior.BillingProviderNPI)) continue;

            foreach (var line in prior.ClaimLines)
            {
                if (!IsRoomAndBoard(line.RevenueCode)) continue;
                var days = (int)Math.Round(line.Units, MidpointRounding.AwayFromZero);
                if (days <= 0) continue;
                var start = line.ServiceDateFrom != default ? line.ServiceDateFrom : prior.ServiceDateFrom;
                result.Add(new OtherInpatientDays { FacilityNpi = prior.BillingProviderNPI.Trim(), From = start.Date, Days = days });
            }
        }
        return result;
    }

    private static bool IsRoomAndBoard(string? revenueCode) =>
        int.TryParse(revenueCode?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
        && code is >= 100 and <= 219 && code is not (>= 180 and <= 189);

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

        // Institutional (837I) claim-level inputs, repeated on every line.
        // The DRG is whichever one InpatientGroupingStage (Order=240) settled
        // on: the one CHO's grouper assigned where the plan prices by the
        // grouped DRG, and the one the facility billed (HI*DR) otherwise,
        // which is what every claim priced before that stage existed. A
        // DRG-contracted claim with no DRG at all finds no DRG rate and pends
        // NOCONTRACT rather than being guessed at. The engine pays a DRG case
        // rate / all-inclusive per diem once per claim, allocated across the
        // lines in proportion to billed charges, so sending the DRG on every
        // line never pays N case rates.
        var billedDrg = string.IsNullOrWhiteSpace(claim.Institutional?.DrgCode)
            ? null
            : claim.Institutional!.DrgCode!.Trim();
        var drgCode = context.InpatientGrouping is { } grouping
            ? grouping.PricedDrg
            : billedDrg;
        // Length of stay only for a stay: 837I DTP*435 (admission date) is
        // required on inpatient claims and absent on outpatient ones, so an
        // outpatient claim never presents a day count that a per-diem
        // contract could price.
        var lengthOfStay = claim.Institutional is { AdmissionDate: not null } inpatient
            ? inpatient.CalculateLengthOfStay()
            : null;
        var billType = claim.TypeOfBill;
        // An 837I claim is a facility setting even when no type of bill could be
        // built (no Institutional.FacilityTypeCode): its place-of-service slot then
        // holds the CLM05-1 facility type ("13"), not a CMS POS. Same claim-type
        // test BenefitCalculationStage uses for PlaceOfServiceIsFacilityType.
        var isInstitutional = claim.ClaimType == ClaimsService.Models.ClaimType.Institutional;

        // State Medicaid APR-DRG inputs. The method is set only for a stay
        // InpatientGroupingStage grouped by APR-DRG on a plan that names one;
        // the state's hospital rates are keyed by the facility, so the billing
        // NPI goes with it. The discharge date picks the rate tables in force,
        // the best available date as in the grouping request.
        var medicaidDrgMethod = context.InpatientGrouping?.MedicaidDrgMethod;
        var inpatientDetails = lengthOfStay is null ? null : claim.Institutional;
        var dischargeDate = inpatientDetails is null
            ? (DateTime?)null
            : inpatientDetails.DischargeDate
                ?? inpatientDetails.StatementToDate
                ?? (claim.ServiceDateTo != default ? claim.ServiceDateTo : claim.ServiceDateFrom);
        var ageAtAdmission = inpatientDetails is null
            ? null
            : InpatientGroupingStage.AgeYearsAt(context.ResolvedMember?.DateOfBirth, inpatientDetails.AdmissionDate);

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
                PlanCode = context.ResolvedPlan?.PlanCode,
                // ChargeAmount is the line total (X12 837 SV102 / SV203), not a
                // per-unit price — same billed amount BenefitCalculationStage
                // reports, so billed − allowed (CO-45) lines up.
                BilledAmount = line.ChargeAmount,
                Units = line.Units,
                LineNumber = line.LineNumber,
                TotalLineCount = totalLines,
                DrgCode = drgCode,
                LengthOfStay = lengthOfStay,
                RevenueCode = string.IsNullOrWhiteSpace(line.RevenueCode) ? null : line.RevenueCode,
                BillType = billType,
                IsInstitutional = isInstitutional,
                MedicaidDrgMethod = medicaidDrgMethod,
                ImdOverLimitAction = context.InpatientGrouping?.ImdOverLimitAction,
                FacilityNpi = string.IsNullOrWhiteSpace(claim.BillingProviderNPI) ? null : claim.BillingProviderNPI,
                DischargeStatus = inpatientDetails?.PatientStatusCode,
                PatientAgeYears = ageAtAdmission,
                DischargeDate = dischargeDate,
                ClaimReceivedDate = ReceivedDate(claim),
            })
            .ToList();
    }

    /// <summary>
    /// The date the plan received the claim, for a supplemental payment's
    /// claims runout: the received date when intake recorded one, otherwise
    /// the submitted date. Null when neither is set.
    /// </summary>
    internal static DateTime? ReceivedDate(AdapterClaim claim) =>
        claim.ReceivedDate ?? (claim.SubmittedDate == default ? null : claim.SubmittedDate);

    /// <summary>
    /// Copies the engine's state supplemental payments and EOB notices onto
    /// the claim's adjudication result (replacing any from an earlier run).
    /// </summary>
    internal static void RecordSupplementalPayments(ClaimAdjudicationContext context, PricingResultSet priced)
    {
        var result = context.AdjudicationResult;
        result.SupplementalPayments = priced.LineResults
            .OrderBy(r => r.LineNumber)
            .SelectMany(r => r.SupplementalPayments.Select(p => new ClaimSupplementalPayment
            {
                LineNumber = r.LineNumber,
                ProcedureCode = r.ProcedureCode,
                FeeScheduleId = p.FeeScheduleId,
                FeeScheduleName = p.FeeScheduleName,
                Rate = p.Rate,
                Units = p.Units,
                Amount = p.Amount,
            }))
            .ToList();
        result.PaymentNotices = priced.LineResults
            .SelectMany(r => r.PaymentNotices)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Copies what priced each line (schedule, revision, rate source, amounts)
    /// onto the claim's adjudication result, replacing any from an earlier
    /// run. Lines pricing returned more than once are left out, as
    /// <see cref="Evaluate"/> leaves them unpriced.
    /// </summary>
    internal static void RecordLinePricing(
        ClaimAdjudicationContext context, IReadOnlyList<PricingRequest> requests, PricingResultSet priced)
    {
        var serviceDates = requests
            .GroupBy(r => r.LineNumber)
            .ToDictionary(g => g.Key, g => g.First().ServiceDate);
        context.AdjudicationResult.LinePricing = priced.LineResults
            .GroupBy(r => r.LineNumber)
            .Where(g => g.Count() == 1)
            .Select(g => g.Single())
            .OrderBy(r => r.LineNumber)
            .Select(r => new ClaimLinePricing
            {
                LineNumber = r.LineNumber,
                ProcedureCode = r.ProcedureCode,
                ServiceDate = serviceDates.TryGetValue(r.LineNumber, out var date) ? date : default,
                FeeScheduleId = r.FeeScheduleId,
                FeeScheduleName = r.FeeScheduleName,
                FeeScheduleRevision = r.FeeScheduleRevision,
                FeeScheduleType = r.FeeScheduleType.ToString(),
                RateSource = r.RateSource.ToString(),
                BaseAmount = r.BaseAmount,
                AllowedAmount = r.AllowedAmount,
                LesserOfBilledApplied = r.LesserOfBilledApplied,
            })
            .ToList();
    }

    internal static PricingOutcome Evaluate(
        AdapterClaim claim,
        IReadOnlyList<PricingRequest> requests,
        PricingResultSet priced)
    {
        // A line number answered more than once is ambiguous — there is no
        // principled way to pick one allowed amount, so treat it as unpriced
        // rather than letting the first (or last) value win.
        var resultsByLine = new Dictionary<int, PricingResult>();
        var duplicateResultLines = new HashSet<int>();
        foreach (var r in priced.LineResults)
        {
            if (!resultsByLine.TryAdd(r.LineNumber, r))
            {
                duplicateResultLines.Add(r.LineNumber);
            }
        }

        var allowed = new Dictionary<int, decimal>();
        var unpriced = new List<UnpricedLine>();

        foreach (var request in requests)
        {
            if (duplicateResultLines.Contains(request.LineNumber))
            {
                unpriced.Add(new UnpricedLine(
                    request.LineNumber, request.ProcedureCode,
                    "pricing returned multiple results for line; allowed amount is ambiguous"));
                continue;
            }

            if (!resultsByLine.TryGetValue(request.LineNumber, out var result))
            {
                unpriced.Add(new UnpricedLine(
                    request.LineNumber, LineLabel(request),
                    "no pricing result returned for line"));
                continue;
            }

            if (result.AllowedAmount < 0)
            {
                unpriced.Add(new UnpricedLine(
                    request.LineNumber, LineLabel(request),
                    $"pricing returned a negative allowed amount ({result.AllowedAmount})"));
                continue;
            }

            if (result.RateSource == RateSource.Unresolved)
            {
                // A rate line matched but the engine could not compute an
                // allowed amount (e.g. percent-of-Medicare with no Medicare
                // reference). It reports $0 — never accept that as a price.
                unpriced.Add(new UnpricedLine(
                    request.LineNumber, LineLabel(request),
                    string.IsNullOrWhiteSpace(result.UnresolvedReason)
                        ? "fee schedule rate could not be resolved"
                        : $"fee schedule rate could not be resolved: {result.UnresolvedReason}"));
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
                    request.LineNumber, LineLabel(request),
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

    /// <summary>
    /// Identifies a line in pend reasons: its procedure code, or its revenue
    /// code for a revenue-code-only institutional line.
    /// </summary>
    private static string LineLabel(PricingRequest request) =>
        string.IsNullOrWhiteSpace(request.ProcedureCode) && !string.IsNullOrWhiteSpace(request.RevenueCode)
            ? $"rev {request.RevenueCode}"
            : request.ProcedureCode;

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
}
