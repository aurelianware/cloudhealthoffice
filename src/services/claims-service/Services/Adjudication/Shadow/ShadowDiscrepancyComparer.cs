using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Stages;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Compares the core's adjudication of a claim (as the CPCDS importer stored
/// it on the claim) with CHO's shadow adjudication of the same claim. Pure:
/// no I/O, so every rule here is unit-tested directly.
/// </summary>
public static class ShadowDiscrepancyComparer
{
    /// <summary>Pend code recorded when CHO denied only because the member is not loaded.</summary>
    public const string MemberNotLoadedCode = "MEMBERNOTLOADED";

    /// <summary>Prefix of the pend code recorded when a CHO stage rejected the claim (it lacks information CHO needs).</summary>
    public const string RejectCodePrefix = "REJECT:";

    // CARC 45 (charge exceeds the fee schedule) is the contractual write-down
    // on every priced line, not a denial; group PR is the member's share.
    private const string ContractualWriteDownCarc = "45";
    private const string PatientResponsibilityGroup = "PR";

    /// <summary>
    /// The core's claim is not final (pended, voided, still in process), so
    /// there is no answer to compare with and CHO is not run.
    /// </summary>
    public static ShadowAdjudicationResult NotComparable(Claim core, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(core);
        return new ShadowAdjudicationResult
        {
            TenantId = tenantId,
            ClaimId = string.IsNullOrEmpty(core.ClaimVersionId) ? core.Id : core.ClaimVersionId,
            ClaimVersionDocumentId = core.Id,
            ClaimNumber = core.ClaimNumber,
            SourceSystem = core.ExternalSource?.SourceSystem,
            SourceClaimId = core.ExternalSource?.SourceClaimId,
            ImportBatchId = core.ExternalSource?.ImportBatchId,
            ClaimType = core.ClaimType.ToString(),
            ServiceDateFrom = core.ServiceDateFrom,
            BillingProviderNpi = core.BillingProviderNPI,
            Kind = ShadowDiscrepancyKind.NotComparable,
            CoreOutcome = ShadowOutcome.NotFinal,
            ChoOutcome = ShadowOutcome.NotFinal,
        };
    }

    public static ShadowAdjudicationResult Compare(
        Claim core, ClaimAdjudicationContext cho, decimal tolerance)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(cho);

        var coreResult = core.AdjudicationResult ?? new AdjudicationResult();
        var choResult = cho.AdjudicationResult ?? new AdjudicationResult();
        var resolved = ClaimAdjudicationStageResult.ResolveOutcome(cho.StageResults);

        var result = new ShadowAdjudicationResult
        {
            TenantId = cho.TenantId,
            ClaimId = string.IsNullOrEmpty(core.ClaimVersionId) ? core.Id : core.ClaimVersionId,
            ClaimVersionDocumentId = core.Id,
            ClaimNumber = core.ClaimNumber,
            SourceSystem = core.ExternalSource?.SourceSystem,
            SourceClaimId = core.ExternalSource?.SourceClaimId,
            ImportBatchId = core.ExternalSource?.ImportBatchId,
            ClaimType = core.ClaimType.ToString(),
            ServiceDateFrom = core.ServiceDateFrom,
            BillingProviderNpi = core.BillingProviderNPI,
            CoreOutcome = CoreOutcome(core.Status),
            ChoOutcome = ChoOutcome(resolved),
            CoreAllowed = coreResult.AllowedAmount,
            CorePaid = coreResult.PayerPayment,
            CoreMemberLiability = coreResult.PatientResponsibility,
            CoreDenialCode = Blank(coreResult.DenialReasonCode),
            CoreNetworkTier = Blank(coreResult.NetworkTier),
            ChoStageFindings = cho.StageResults
                .Where(r => r.Outcome != ClaimAdjudicationOutcome.Pass)
                .Select(r => string.IsNullOrWhiteSpace(r.Reason) ? r.StageName : $"{r.StageName}: {r.Reason}")
                .ToList(),
        };

        var choDecided = resolved is ClaimAdjudicationOutcome.Pass or ClaimAdjudicationOutcome.Deny;
        var memberNotLoaded = resolved == ClaimAdjudicationOutcome.Deny
                              && cho.MemberLookup == MemberLookupOutcome.NotFound;

        if (resolved == ClaimAdjudicationOutcome.Pend)
        {
            result.ChoPendCode = cho.PendDetails?.PendCode;
            result.ChoPendReason = cho.PendDetails?.PendReason
                                   ?? cho.StageResults.FirstOrDefault(r => r.Outcome == ClaimAdjudicationOutcome.Pend)?.Reason;
        }
        else if (resolved == ClaimAdjudicationOutcome.Reject)
        {
            var rejecting = cho.StageResults.First(r => r.Outcome == ClaimAdjudicationOutcome.Reject);
            result.ChoPendCode = RejectCodePrefix + rejecting.StageName;
            result.ChoPendReason = rejecting.Reason;
        }
        else if (memberNotLoaded)
        {
            result.ChoPendCode = MemberNotLoadedCode;
            result.ChoPendReason = "The member is not loaded in CHO (load the plan's 834 or CPCDS member file).";
        }

        if (choDecided && !memberNotLoaded)
        {
            result.ChoAllowed = choResult.AllowedAmount;
            result.ChoPaid = choResult.PayerPayment;
            result.ChoMemberLiability = choResult.PatientResponsibility;
            result.ChoNetworkTier = Blank(choResult.NetworkTier);
            result.ChoDenialCode = resolved == ClaimAdjudicationOutcome.Deny
                ? Blank(choResult.DenialReasonCode) ?? AdjudicationDenialCodes.DefaultDenyCarc
                : null;
        }

        result.Lines = CompareLines(core, cho, coreResult, result, choDecided && !memberNotLoaded, tolerance);
        result.Kind = Classify(result, choDecided && !memberNotLoaded, tolerance);

        if (result.Kind is not (ShadowDiscrepancyKind.NotComparable or ShadowDiscrepancyKind.ChoUndecided))
        {
            result.PaidVariance = result.ChoPaid - result.CorePaid;
            result.AbsolutePaidVariance = Math.Abs(result.PaidVariance);
            result.AllowedVariance = result.ChoAllowed - result.CoreAllowed;
        }

        return result;
    }

    private static List<ShadowLineComparison> CompareLines(
        Claim core, ClaimAdjudicationContext cho, AdjudicationResult coreResult,
        ShadowAdjudicationResult claim, bool choDecided, decimal tolerance)
    {
        var lines = new List<ShadowLineComparison>(core.ClaimLines.Count);
        var coreClaimDenied = claim.CoreOutcome == ShadowOutcome.Denied;
        var choClaimDenied = claim.ChoOutcome == ShadowOutcome.Denied;

        for (var i = 0; i < core.ClaimLines.Count; i++)
        {
            var coreLine = core.ClaimLines[i];
            var coreAmounts = coreLine.AdjudicationResult;
            var choAmounts = choDecided && i < cho.LineAdjudicationResults.Count ? cho.LineAdjudicationResults[i] : null;

            var line = new ShadowLineComparison
            {
                LineNumber = coreLine.LineNumber,
                ProcedureCode = coreLine.ProcedureCode,
                RevenueCode = coreLine.RevenueCode,
                Charge = coreLine.ChargeAmount,
                CoreAmountsPresent = coreAmounts is not null,
                CoreAllowed = coreAmounts?.AllowedAmount,
                CorePaid = coreAmounts?.PaidAmount,
                CoreMemberLiability = coreAmounts?.PatientResponsibility,
                CoreDenialCode = CoreLineDenialCode(coreResult, coreLine.LineNumber)
                                 ?? (coreClaimDenied ? claim.CoreDenialCode : null),
                ChoAllowed = choAmounts?.AllowedAmount,
                ChoPaid = choAmounts?.PaidAmount,
                ChoMemberLiability = choAmounts?.PatientResponsibility,
            };

            if (choDecided)
            {
                line.ChoDenialCode = (choAmounts is null ? null : ChoLineDenialCode(choAmounts))
                                     ?? (choClaimDenied ? claim.ChoDenialCode : null);
            }

            if (choDecided && claim.CoreOutcome != ShadowOutcome.NotFinal)
            {
                if (coreAmounts is not null && choAmounts is not null)
                {
                    if (Differs(coreAmounts.AllowedAmount, choAmounts.AllowedAmount, tolerance))
                        line.Differences.Add(ShadowLineDifference.Allowed);
                    if (Differs(coreAmounts.PaidAmount, choAmounts.PaidAmount, tolerance))
                        line.Differences.Add(ShadowLineDifference.Paid);
                    if (Differs(coreAmounts.PatientResponsibility, choAmounts.PatientResponsibility, tolerance))
                        line.Differences.Add(ShadowLineDifference.MemberLiability);
                }

                var coreLineDenied = line.CoreDenialCode is not null;
                var choLineDenied = line.ChoDenialCode is not null;
                if (coreLineDenied != choLineDenied)
                    line.Differences.Add(ShadowLineDifference.LineOutcome);
                else if (coreLineDenied && !SameCode(line.CoreDenialCode, line.ChoDenialCode))
                    line.Differences.Add(ShadowLineDifference.DenialCode);
            }

            lines.Add(line);
        }

        return lines;
    }

    private static string Classify(ShadowAdjudicationResult r, bool choDecided, decimal tolerance)
    {
        if (r.CoreOutcome == ShadowOutcome.NotFinal) return ShadowDiscrepancyKind.NotComparable;
        if (!choDecided) return ShadowDiscrepancyKind.ChoUndecided;
        if (r.CoreOutcome != r.ChoOutcome) return ShadowDiscrepancyKind.OutcomeMismatch;

        if (r.CoreOutcome == ShadowOutcome.Denied)
        {
            return SameCode(r.CoreDenialCode, r.ChoDenialCode)
                ? ShadowDiscrepancyKind.Match
                : ShadowDiscrepancyKind.DenialCodeMismatch;
        }

        bool AnyLine(string difference) => r.Lines.Any(l => l.Differences.Contains(difference));

        if (Differs(r.CoreAllowed, r.ChoAllowed, tolerance) || AnyLine(ShadowLineDifference.Allowed))
            return ShadowDiscrepancyKind.AllowedVariance;
        if (Differs(r.CorePaid, r.ChoPaid, tolerance) || AnyLine(ShadowLineDifference.Paid)
            || AnyLine(ShadowLineDifference.LineOutcome))
            return ShadowDiscrepancyKind.PaidVariance;
        if (Differs(r.CoreMemberLiability, r.ChoMemberLiability, tolerance) || AnyLine(ShadowLineDifference.MemberLiability))
            return ShadowDiscrepancyKind.MemberLiabilityVariance;
        if (AnyLine(ShadowLineDifference.DenialCode))
            return ShadowDiscrepancyKind.DenialCodeMismatch;
        return ShadowDiscrepancyKind.Match;
    }

    internal static string CoreOutcome(ClaimStatus status) => status switch
    {
        ClaimStatus.Paid or ClaimStatus.PartiallyPaid or ClaimStatus.Approved => ShadowOutcome.Paid,
        ClaimStatus.Denied or ClaimStatus.Deny => ShadowOutcome.Denied,
        _ => ShadowOutcome.NotFinal,
    };

    private static string ChoOutcome(ClaimAdjudicationOutcome outcome) => outcome switch
    {
        ClaimAdjudicationOutcome.Pass => ShadowOutcome.Paid,
        ClaimAdjudicationOutcome.Deny => ShadowOutcome.Denied,
        ClaimAdjudicationOutcome.Reject => ShadowOutcome.Rejected,
        _ => ShadowOutcome.Pended,
    };

    /// <summary>The CPCDS importer stores a line's denial code as a claim adjustment described "Line N".</summary>
    private static string? CoreLineDenialCode(AdjudicationResult core, int lineNumber)
    {
        var label = $"Line {lineNumber}";
        return Blank(core.AdjustmentReasons
            .FirstOrDefault(a => string.Equals(a.Description, label, StringComparison.Ordinal))?.ReasonCode);
    }

    /// <summary>
    /// A CHO line is denied when it carries an adjustment that is neither the
    /// member's share (group PR) nor the contractual write-down (CARC 45).
    /// </summary>
    private static string? ChoLineDenialCode(LineAdjudicationResult line) =>
        Blank(line.AdjustmentReasons
            .FirstOrDefault(a => !string.Equals(a.GroupCode, PatientResponsibilityGroup, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(a.ReasonCode?.Trim(), ContractualWriteDownCarc, StringComparison.Ordinal))
            ?.ReasonCode);

    private static bool Differs(decimal a, decimal b, decimal tolerance) => Math.Abs(a - b) > tolerance;

    private static bool SameCode(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
