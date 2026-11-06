using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
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

    /// <summary>
    /// Recorded when CHO's only hold is the provider screening it could not complete
    /// (see <see cref="ShadowAdjudicationResult.HeldForProviderScreening"/>).
    /// </summary>
    public const string ProviderScreeningCode = "PROVIDERSCREEN";

    /// <summary>Recorded when benefit calculation could not run because the claim reached it with no benefit plan.</summary>
    public const string NoPlanCode = "PLANUNAVAIL";

    /// <summary>
    /// Recorded when CHO denied only because the plan's network or the provider's
    /// credentialing was never loaded (the same code a network pend carries).
    /// </summary>
    public const string NetworkSetupCode = "NETWORK";

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

        // A provider CHO could not screen holds the claim for review; nothing is
        // wrong with the claim itself, and a fresh deployment has screened no one.
        // Compare what CHO decides once the provider clears, and say it is held.
        // A confirmed exclusion is a Deny and is compared as one.
        var heldForScreening = false;
        if (resolved == ClaimAdjudicationOutcome.Pend)
        {
            var withoutScreening = ClaimAdjudicationStageResult.ResolveOutcome(cho.StageResults
                .Where(r => !(r.Outcome == ClaimAdjudicationOutcome.Pend
                              && r.StageName == ProviderIntegrityStage.StageName))
                .ToList());
            if (withoutScreening is ClaimAdjudicationOutcome.Pass or ClaimAdjudicationOutcome.Deny)
            {
                resolved = withoutScreening;
                heldForScreening = true;
            }
        }

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
            HeldForProviderScreening = heldForScreening,
        };
        if (heldForScreening)
        {
            result.ChoPendCode = ProviderScreeningCode;
            result.ChoPendReason = cho.StageResults
                .First(r => r.Outcome == ClaimAdjudicationOutcome.Pend && r.StageName == ProviderIntegrityStage.StageName)
                .Reason;
        }

        var memberNotLoaded = resolved == ClaimAdjudicationOutcome.Deny
                              && cho.MemberLookup == MemberLookupOutcome.NotFound;
        var setupGap = resolved == ClaimAdjudicationOutcome.Deny && !memberNotLoaded
            ? NetworkSetupGap(cho)
            : null;
        var choDecided = (resolved is ClaimAdjudicationOutcome.Pass or ClaimAdjudicationOutcome.Deny)
                         && setupGap is null;

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

            // No plan to calculate with: the member's coverage named no CHO plan,
            // almost always a core plan code with no mapping. Say so, not "rejected".
            if (rejecting.StageName == BenefitCalculationStage.StageName
                && string.IsNullOrWhiteSpace(cho.Claim.BenefitPlanId))
            {
                result.ChoPendCode = NoPlanCode;
                result.ChoPendReason = "The member's coverage names no CHO benefit plan: create the plan and map the core's plan code to it.";
            }
        }
        else if (memberNotLoaded)
        {
            result.ChoPendCode = MemberNotLoadedCode;
            result.ChoPendReason = "The member is not loaded in CHO (load the plan's 834 or CPCDS member file).";
        }
        else if (setupGap is { } gap)
        {
            result.ChoPendCode = gap.Code;
            result.ChoPendReason = gap.Reason;
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

    /// <summary>
    /// A fail-closed network or credentialing denial that comes from data CHO
    /// was never given, not from a finding: the claim's plan is unknown, the plan
    /// has no network configured, or the provider has no credentialing record
    /// at all. A shadow run on a fresh deployment (or any deployment that runs
    /// FailClosed, as Testing and Production hosts do) would otherwise report
    /// every such claim as CHO denying what the core paid. A provider that is
    /// in none of the plan's networks, or whose credentialing is pending,
    /// denied, expired or suspended, is a finding and stays a denial.
    /// </summary>
    private static (string Code, string Reason)? NetworkSetupGap(ClaimAdjudicationContext cho)
    {
        var denyingStages = cho.StageResults.Where(r => r.Outcome == ClaimAdjudicationOutcome.Deny).ToList();
        if (denyingStages.Count == 0
            || denyingStages.Any(r => r.StageName != NetworkCredentialingStage.StageName))
            return null;

        var denials = cho.EnforcementOutcomes.Where(o => o.Decision == EnforcementDecision.Deny).ToList();
        if (denials.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(cho.Claim.BenefitPlanId) || cho.ResolvedPlan is null)
        {
            return (NoPlanCode,
                "The member's coverage names no CHO benefit plan, so CHO has no network to check the provider against: create the plan and map the core's plan code to it.");
        }

        var planHasNoNetwork = cho.ResolvedPlan.NetworkTiers.All(t => string.IsNullOrWhiteSpace(t.NetworkId));
        var credentialing = new[] { cho.BillingProviderCredentialingStatus, cho.RenderingProviderCredentialingStatus }
            .Where(s => s is not null && !s.IsApprovedAtAsOf)
            .ToList();
        var noCredentialingRecord = credentialing.Count > 0
            && credentialing.All(s => string.Equals(s!.Status, "Unknown", StringComparison.OrdinalIgnoreCase));

        foreach (var denial in denials)
        {
            var isGap = denial.Check switch
            {
                EnforcementCheck.Membership => planHasNoNetwork,
                EnforcementCheck.Credentialing => noCredentialingRecord,
                _ => false,
            };
            if (!isGap) return null;
        }

        return denials.Any(d => d.Check == EnforcementCheck.Membership)
            ? (NetworkSetupCode,
                "The member's benefit plan has no network in CHO: give its in-network tier a network and load the core's network roster.")
            : (NetworkSetupCode,
                "CHO has no credentialing record for the provider: load the core's network roster with its credentialed column (or record the credentialing decision).");
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
