using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// Builds one claim's 835 2100/2110 data (<see cref="ClaimPayment"/>) from
/// claims-service's claim. Pure (no I/O) so payment runs, the run-time
/// balance pre-check and contract tests build exactly the same thing.
///
/// Adjustments:
/// <list type="bullet">
/// <item>Lines remitted with SVC loops carry their own CAS: the line's
/// adjudication adjustments (<c>claimLines[].adjudicationResult.adjustmentReasons</c>:
/// CO-45, PR-1/2/3, OA-23, CO denial CARCs, with an optional RARC that goes
/// to LQ*HE). NCCI edit CARCs for the line (<c>pendDetails.editFailures</c>)
/// are added only when the line does not already carry that group and CARC
/// (their RARC is kept on the matching adjustment). A line with no
/// adjudication adjustments (claims adjudicated before claims-service
/// populated them) gets, on a single-line claim, the claim-level
/// adjustments; otherwise the amount SVC02 - SVC03 leaves unexplained goes
/// to the line's NCCI edit CARC if it has one, else to one CO adjustment
/// (CO-45, or CO with the denial CARC on a denied claim) so it balances.</item>
/// <item>When lines carry the adjustments, the claim-level adjustments
/// (<c>adjudicationResult.adjustmentReasons</c>, the claim's totals of the
/// same amounts) are not repeated in the header CAS; only a denial CARC no
/// line carries stays there. So CLP03 - sum(CAS, claim and lines) = CLP04.</item>
/// <item>A claim remitted at claim level (no SVC) keeps the claim-level
/// adjustments in its header CAS.</item>
/// </list>
/// </summary>
public static class Era835ClaimPaymentBuilder
{
    /// <summary>
    /// True when the claim is remitted at claim level, without SVC loops: it
    /// has no service lines, or none of them carries a paid amount (the claim
    /// was adjudicated at claim level only: payerPayment without line results).
    /// </summary>
    public static bool RemitsAtClaimLevel(ClaimDto claim) =>
        claim.ServiceLines is not { Count: > 0 } lines || lines.All(sl => sl.LinePaidAmount is null);

    /// <summary>
    /// One claim's 2100 loop. A paid claim: CLP02 = 1, CLP04 = its plan-paid
    /// amount. A denial: CLP02 = 4, CLP04 = 0, its denial CARC carrying the
    /// part of the charge no other adjustment explains, RARCs in MIA/MOA.
    /// </summary>
    public static ClaimPayment Build(ClaimDto claim, bool denied, ICarcRarcMappingService mapper)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(mapper);

        var snapshot = BuildAdjudicationSnapshot(claim);
        var headerCas = mapper.MapClaimAdjustments(snapshot).ToList();
        var editCas = mapper.MapLineAdjustments(snapshot);
        var denialCode = claim.AdjudicationResult?.DenialReasonCode;

        // An older single-line claim without line adjustment detail: its
        // claim-level adjustments are that line's adjustments.
        var claimLevelForSingleLine =
            !RemitsAtClaimLevel(claim)
            && claim.ServiceLines!.Count == 1
            && claim.ServiceLines[0].AdjudicationResult?.AdjustmentReasons is not { Count: > 0 }
            && claim.AdjudicationResult?.AdjustmentReasons is { Count: > 0 }
                ? claim.AdjudicationResult.AdjustmentReasons
                    .Select(r => new ClaimLineAdjustmentReasonDto
                    {
                        GroupCode = r.GroupCode, ReasonCode = r.ReasonCode, Amount = r.Amount, Description = r.Description,
                    })
                    .ToList()
                : null;

        var serviceLines = RemitsAtClaimLevel(claim)
            ? new List<ServiceLinePayment>()
            : claim.ServiceLines!
                .Select(sl =>
                {
                    // Never the line charge: the recorded paid amount, or 0
                    // (the run kept the claim only if the lines balance to
                    // CLP04 with 0 for unpriced lines).
                    var paid = sl.LinePaidAmount ?? 0m;
                    var adjustments = LineAdjustments(
                        sl, paid, claimLevelForSingleLine,
                        editCas.TryGetValue(sl.LineNumber, out var edits) ? edits : Array.Empty<ServiceLineAdjustment>(),
                        denied && !string.IsNullOrWhiteSpace(denialCode) ? denialCode! : "45",
                        out var extraRemarks);
                    return new ServiceLinePayment
                    {
                        LineNumber = sl.LineNumber,
                        ProcedureCode = sl.ProcedureCode,
                        Modifiers = sl.Modifiers?.ToList() ?? new List<string>(),
                        ChargeAmount = sl.ChargeAmount,
                        PaymentAmount = paid,
                        RevenueCode = sl.RevenueCode,
                        Units = sl.Units,
                        ServiceDateFrom = sl.ServiceDateFrom,
                        ServiceDateTo = sl.ServiceDateTo,
                        Adjustments = adjustments,
                        RemarkCodes = extraRemarks,
                    };
                })
                .ToList();

        if (serviceLines.Count > 0)
        {
            // The lines explain the charge; keep only a denial CARC no line carries.
            var lineCodes = new HashSet<string>(
                serviceLines.SelectMany(l => l.Adjustments).Select(a => a.ReasonCode), StringComparer.Ordinal);
            var claimLevelCodes = new HashSet<string>(
                snapshot.AdjustmentReasons.Select(r => r.ReasonCode), StringComparer.Ordinal);
            headerCas = headerCas
                .Where(a => !string.IsNullOrEmpty(denialCode)
                    && string.Equals(a.ReasonCode, denialCode, StringComparison.Ordinal)
                    && !claimLevelCodes.Contains(a.ReasonCode)
                    && !lineCodes.Contains(a.ReasonCode))
                .ToList();
        }

        var claimPaid = denied ? 0m : PlanPaidAmountOf(claim);
        var isInstitutional = claim.ClaimType == ClaimFormType.Institutional;
        if (denied)
            headerCas = WithDenialAmount(headerCas, claim, serviceLines);
        if (serviceLines.Count > 0)
            headerCas.RemoveAll(a => a.Amount == 0m); // the lines already explain the charge

        return new ClaimPayment
        {
            ClaimId = claim.Id,
            PatientControlNumber = claim.ClaimNumber,
            // CLP02: 1 = processed as primary, 4 = denied.
            ClaimStatusCode = denied ? "4" : "1",
            // CLP03 total charge, CLP04 plan paid (0 for a denial), CLP05 member responsibility.
            ChargeAmount = claim.TotalChargeAmount,
            PaymentAmount = claimPaid,
            PatientResponsibilityAmount = claim.AdjudicationResult?.PatientResponsibility ?? 0m,
            PayerClaimControlNumber = claim.PayerClaimControlNumber,
            IsInstitutional = isInstitutional,
            // CLP08 / CLP09 / CLP11: reported for an institutional claim only.
            FacilityTypeCode = isInstitutional ? Code(claim.Institutional?.FacilityTypeCode) : null,
            ClaimFrequencyCode = isInstitutional ? Code(claim.ClaimFrequencyCode) : null,
            DrgCode = isInstitutional ? Code(claim.Institutional?.DrgCode) : null,
            MemberId = claim.MemberId,
            RenderingProviderNPI = claim.RenderingProviderNPI,
            ClaimAdjustments = headerCas,
            RemarkCodes = denied ? snapshot.RemarkCodes.ToList() : new List<string>(),
            ServiceLines = serviceLines
        };
    }

    /// <summary>
    /// The reversal (CLP02 = 22) of a recorded claim payment: the original
    /// 2100/2110 loops with CLP03, CLP04, CLP05, SVC02, SVC03 and every CAS
    /// amount negated, so each line still balances (SVC02 - sum(line CAS) =
    /// SVC03) and the claim does too (CLP03 - sum(CAS) = CLP04), in the
    /// negative. Remark codes (MOA/MIA, LQ*HE) and identifiers are repeated.
    ///
    /// The adjustments are the recorded ones when the original was remitted
    /// with them (its lines carry CAS, or, remitted at claim level, its header
    /// does). An original recorded without that detail (paid before line CAS
    /// existed) gets the adjustments <see cref="Build"/> derives for it from
    /// <paramref name="predecessor"/>, with the recorded charge and paid
    /// amounts: the same fallbacks a payment gets (single-line claim: the
    /// claim-level adjustments; otherwise the line's NCCI edit CARC, else
    /// CO-45, or CO with the denial CARC on a denial).
    /// </summary>
    public static ClaimPayment BuildReversal(
        ClaimPayment original, ClaimDto predecessor, ICarcRarcMappingService mapper)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(predecessor);
        ArgumentNullException.ThrowIfNull(mapper);

        var source = RecordedWithAdjustments(original) ? original : WithDerivedAdjustments(original, predecessor, mapper);
        return WithInstitutionalCodes(Negated(source), predecessor);
    }

    /// <summary>
    /// The reversal's CLP08 / CLP09 / CLP11 are the original's. An
    /// institutional original recorded before they were carried gets them
    /// from the predecessor claim (the same claim), so the reversal still
    /// reports the required facility type and frequency codes.
    /// </summary>
    private static ClaimPayment WithInstitutionalCodes(ClaimPayment reversal, ClaimDto predecessor)
    {
        if (!reversal.IsInstitutional)
            return reversal;
        reversal.FacilityTypeCode ??= Code(predecessor.Institutional?.FacilityTypeCode);
        reversal.ClaimFrequencyCode ??= Code(predecessor.ClaimFrequencyCode);
        reversal.DrgCode ??= Code(predecessor.Institutional?.DrgCode);
        return reversal;
    }

    /// <summary>A code as reported, or null when blank.</summary>
    private static string? Code(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// True when the recorded claim payment carries its adjustments: a line
    /// with CAS, or, remitted at claim level, a header CAS.
    /// </summary>
    public static bool RecordedWithAdjustments(ClaimPayment cp) =>
        cp.ServiceLines.Count > 0
            ? cp.ServiceLines.Any(l => l.Adjustments.Count > 0)
            : cp.ClaimAdjustments.Count > 0;

    /// <summary>
    /// <paramref name="original"/> with the adjustments <see cref="Build"/>
    /// derives from the predecessor claim, priced at what was actually
    /// recorded: the recorded lines (charge, paid, codes, dates), the recorded
    /// CLP03/CLP04/CLP05, the line adjustments claims-service holds for those
    /// lines (none on older claims, so the fallbacks apply).
    /// </summary>
    private static ClaimPayment WithDerivedAdjustments(
        ClaimPayment original, ClaimDto predecessor, ICarcRarcMappingService mapper)
    {
        var denied = original.ClaimStatusCode == "4";
        var predLines = (predecessor.ServiceLines ?? new List<ClaimServiceLineDto>())
            .GroupBy(l => l.LineNumber)
            .ToDictionary(g => g.Key, g => g.First());
        var adjudication = predecessor.AdjudicationResult;

        var priced = new ClaimDto
        {
            Id = original.ClaimId,
            ClaimNumber = original.PatientControlNumber,
            MemberId = original.MemberId ?? predecessor.MemberId,
            BillingProviderNPI = predecessor.BillingProviderNPI,
            PayerClaimControlNumber = original.PayerClaimControlNumber,
            RenderingProviderNPI = original.RenderingProviderNPI,
            TotalChargeAmount = original.ChargeAmount,
            ClaimType = original.IsInstitutional ? ClaimFormType.Institutional : predecessor.ClaimType,
            PendDetails = predecessor.PendDetails,
            AdjudicationResult = new ClaimAdjudicationDto
            {
                PayerPayment = original.PaymentAmount,
                PatientResponsibility = original.PatientResponsibilityAmount,
                DenialReasonCode = adjudication?.DenialReasonCode,
                DenialReason = adjudication?.DenialReason,
                AdjustmentReasons = adjudication?.AdjustmentReasons,
                RemarkCodes = adjudication?.RemarkCodes,
            },
            ServiceLines = original.ServiceLines.Count == 0
                ? null
                : original.ServiceLines
                    .Select(sl => new ClaimServiceLineDto
                    {
                        LineNumber = sl.LineNumber,
                        ProcedureCode = sl.ProcedureCode,
                        Modifiers = sl.Modifiers.ToList(),
                        ChargeAmount = sl.ChargeAmount,
                        PaidAmount = sl.PaymentAmount,
                        RevenueCode = sl.RevenueCode,
                        Units = sl.Units,
                        ServiceDateFrom = sl.ServiceDateFrom,
                        ServiceDateTo = sl.ServiceDateTo,
                        AdjudicationResult = new ClaimLineAdjudicationDto
                        {
                            PaidAmount = sl.PaymentAmount,
                            AdjustmentReasons = predLines.TryGetValue(sl.LineNumber, out var pl)
                                ? pl.AdjudicationResult?.AdjustmentReasons
                                : null,
                        },
                    })
                    .ToList(),
        };

        var derived = Build(priced, denied, mapper);
        var result = Copy(original);
        result.ClaimAdjustments = derived.ClaimAdjustments;
        if (result.RemarkCodes.Count == 0)
            result.RemarkCodes = derived.RemarkCodes;
        for (var i = 0; i < result.ServiceLines.Count; i++)
        {
            result.ServiceLines[i].Adjustments = derived.ServiceLines[i].Adjustments;
            result.ServiceLines[i].RemarkCodes = result.ServiceLines[i].RemarkCodes
                .Concat(derived.ServiceLines[i].RemarkCodes)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        return result;
    }

    /// <summary>The CLP02 = 22 copy of <paramref name="cp"/>, every amount negated.</summary>
    private static ClaimPayment Negated(ClaimPayment cp)
    {
        var r = Copy(cp);
        r.ClaimStatusCode = "22";
        r.ChargeAmount = Neg(cp.ChargeAmount);
        r.PaymentAmount = Neg(cp.PaymentAmount);
        r.PatientResponsibilityAmount = Neg(cp.PatientResponsibilityAmount);
        foreach (var a in r.ClaimAdjustments)
            a.Amount = Neg(a.Amount);
        foreach (var line in r.ServiceLines)
        {
            line.ChargeAmount = Neg(line.ChargeAmount);
            line.PaymentAmount = Neg(line.PaymentAmount);
            foreach (var a in line.Adjustments)
                a.Amount = Neg(a.Amount);
        }
        return r;
    }

    /// <summary>Negated, never "-0.00".</summary>
    private static decimal Neg(decimal amount) => amount == 0m ? 0m : -amount;

    /// <summary>A deep copy of the 835 data of <paramref name="cp"/> (not its finalize state).</summary>
    private static ClaimPayment Copy(ClaimPayment cp) => new()
    {
        ClaimId = cp.ClaimId,
        PatientControlNumber = cp.PatientControlNumber,
        ClaimStatusCode = cp.ClaimStatusCode,
        ChargeAmount = cp.ChargeAmount,
        PaymentAmount = cp.PaymentAmount,
        PatientResponsibilityAmount = cp.PatientResponsibilityAmount,
        PayerClaimControlNumber = cp.PayerClaimControlNumber,
        MemberId = cp.MemberId,
        IsInstitutional = cp.IsInstitutional,
        FacilityTypeCode = cp.FacilityTypeCode,
        ClaimFrequencyCode = cp.ClaimFrequencyCode,
        DrgCode = cp.DrgCode,
        ClaimReceivedDate = cp.ClaimReceivedDate,
        RenderingProviderNPI = cp.RenderingProviderNPI,
        RemarkCodes = cp.RemarkCodes.ToList(),
        ClaimAdjustments = cp.ClaimAdjustments
            .Select(a => new ClaimAdjustment
            {
                GroupCode = a.GroupCode, ReasonCode = a.ReasonCode, Amount = a.Amount, ReasonDescription = a.ReasonDescription,
            })
            .ToList(),
        ServiceLines = cp.ServiceLines
            .Select(sl => new ServiceLinePayment
            {
                LineNumber = sl.LineNumber,
                ProcedureCode = sl.ProcedureCode,
                // SVC01 of the reversal identifies the same billed service as
                // the original (HC:code:modifiers / NU:rev, SVC04 rev).
                Modifiers = sl.Modifiers.ToList(),
                ChargeAmount = sl.ChargeAmount,
                PaymentAmount = sl.PaymentAmount,
                RevenueCode = sl.RevenueCode,
                Units = sl.Units,
                ServiceDateFrom = sl.ServiceDateFrom,
                ServiceDateTo = sl.ServiceDateTo,
                RemarkCodes = sl.RemarkCodes.ToList(),
                Adjustments = sl.Adjustments
                    .Select(a => new ServiceLineAdjustment
                    {
                        GroupCode = a.GroupCode,
                        ReasonCode = a.ReasonCode,
                        Amount = a.Amount,
                        Quantity = a.Quantity,
                        RemarkCode = a.RemarkCode,
                        ReasonDescription = a.ReasonDescription,
                    })
                    .ToList(),
            })
            .ToList(),
    };

    /// <summary>
    /// A line's CAS: its adjudication adjustments, plus NCCI edit CARCs it does
    /// not already carry; with no adjudication adjustments, one CO
    /// (<paramref name="fallbackCarc"/>) for whatever SVC02 - SVC03 the edits
    /// leave unexplained.
    /// </summary>
    private static List<ServiceLineAdjustment> LineAdjustments(
        ClaimServiceLineDto line, decimal paid, List<ClaimLineAdjustmentReasonDto>? claimLevelForSingleLine,
        IReadOnlyList<ServiceLineAdjustment> edits, string fallbackCarc, out List<string> extraRemarks)
    {
        extraRemarks = new List<string>();
        var reasons = line.AdjudicationResult?.AdjustmentReasons is { Count: > 0 } own
            ? own
            : claimLevelForSingleLine ?? new List<ClaimLineAdjustmentReasonDto>();
        var adjustments = reasons
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.GroupCode) && !string.IsNullOrWhiteSpace(r.ReasonCode))
            .Select(r => new ServiceLineAdjustment
            {
                GroupCode = r.GroupCode,
                ReasonCode = r.ReasonCode,
                Amount = r.Amount,
                RemarkCode = string.IsNullOrWhiteSpace(r.RemarkCode) ? null : r.RemarkCode,
                ReasonDescription = r.Description,
            })
            .ToList();
        // Only the line's own adjudication adjustments are complete; anything
        // else (claim-level ones moved onto a single line, NCCI edits) gets
        // the fallback for what it leaves unexplained.
        var fromAdjudication = line.AdjudicationResult?.AdjustmentReasons is { Count: > 0 };

        foreach (var edit in edits)
        {
            var same = adjustments.FirstOrDefault(a =>
                string.Equals(a.GroupCode, edit.GroupCode, StringComparison.Ordinal)
                && string.Equals(a.ReasonCode, edit.ReasonCode, StringComparison.Ordinal));
            if (same is not null)
            {
                // The money is counted once (the matching adjustment); the
                // edit's RARC is kept: on the adjustment if it has none, else
                // with the line's other remarks (every distinct RARC reaches LQ*HE).
                if (string.IsNullOrWhiteSpace(edit.RemarkCode))
                    continue;
                if (same.RemarkCode is null)
                    same.RemarkCode = edit.RemarkCode;
                else if (!string.Equals(same.RemarkCode, edit.RemarkCode, StringComparison.Ordinal)
                         && !extraRemarks.Contains(edit.RemarkCode!, StringComparer.Ordinal))
                    extraRemarks.Add(edit.RemarkCode!);
                continue;
            }
            adjustments.Add(new ServiceLineAdjustment
            {
                GroupCode = edit.GroupCode,
                ReasonCode = edit.ReasonCode,
                Amount = edit.Amount,
                RemarkCode = edit.RemarkCode,
                ReasonDescription = edit.ReasonDescription,
            });
        }

        if (!fromAdjudication)
        {
            var unexplained = line.ChargeAmount - paid - adjustments.Sum(a => a.Amount);
            // An NCCI edit on the line is why it was cut: the first adjustment
            // whose group and CARC an edit names carries the unexplained
            // amount (never some other zero-amount adjustment, e.g. an OA-23).
            // Otherwise one CO fallback.
            var edit = adjustments.FindIndex(a => edits.Any(e =>
                string.Equals(e.GroupCode, a.GroupCode, StringComparison.Ordinal)
                && string.Equals(e.ReasonCode, a.ReasonCode, StringComparison.Ordinal)));
            if (unexplained != 0m && edit >= 0)
            {
                adjustments[edit].Amount += unexplained;
            }
            else if (unexplained != 0m)
            {
                adjustments.Add(new ServiceLineAdjustment
                {
                    GroupCode = "CO",
                    ReasonCode = fallbackCarc,
                    Amount = unexplained,
                    ReasonDescription = "Line adjudicated without adjustment detail: charge less payment",
                });
            }
        }

        return adjustments;
    }

    /// <summary>
    /// On a denial the denial CARC explains the charge the plan did not pay:
    /// the header entry with that CARC, whatever amount it arrived with (the
    /// mapper's synthetic 0, or a claim-level adjustment carrying the same
    /// CARC), carries what the other adjustments leave unexplained (charge -
    /// 0 paid - the other CAS, claim and lines), so the CAS balances.
    /// </summary>
    public static List<ClaimAdjustment> WithDenialAmount(
        List<ClaimAdjustment> headerCas, ClaimDto claim, List<ServiceLinePayment> serviceLines)
    {
        var code = claim.AdjudicationResult?.DenialReasonCode;
        if (string.IsNullOrEmpty(code))
            return headerCas;
        var index = headerCas.FindIndex(a => string.Equals(a.ReasonCode, code, StringComparison.Ordinal));
        if (index < 0)
            return headerCas;

        var others = headerCas.Where((_, i) => i != index).Sum(a => a.Amount);
        var unexplained = claim.TotalChargeAmount
            - others
            - serviceLines.Sum(l => l.Adjustments.Sum(a => a.Amount));

        var denial = headerCas[index];
        headerCas[index] = new ClaimAdjustment
        {
            GroupCode = denial.GroupCode,
            ReasonCode = denial.ReasonCode,
            Amount = Math.Max(unexplained, 0m),
            ReasonDescription = denial.ReasonDescription,
        };
        return headerCas;
    }

    /// <summary>
    /// The amount a claim is paid: the plan's payment. Claims without one are
    /// excluded before reservation; reaching here without one is a bug, and
    /// the run fails rather than paying billed charges.
    /// </summary>
    public static decimal PlanPaidAmountOf(ClaimDto claim) =>
        claim.PlanPaidAmount
        ?? throw new InvalidOperationException(
            $"Claim {claim.Id} has no plan-paid amount; it is never paid at billed charges");

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
}
