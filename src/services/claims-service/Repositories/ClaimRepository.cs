using ClaimsService.Models;

namespace ClaimsService.Repositories;

/// <summary>
/// Claim status and version rules shared by <see cref="ClaimRepositoryMongo"/>,
/// the adjudication pipeline and tests: the status → version-state mapping,
/// the final-disposition and synchronous write-back guards, the narrow
/// contradiction repairs and legacy-row hydration. MongoDB is the only claim
/// store; the name is kept from the removed Cosmos repository so callers did
/// not change.
/// </summary>
public static class ClaimRepository
{
    /// <summary>
    /// Hydrates legacy claim documents (predating versioning fields) with
    /// sensible defaults. Idempotent — running on a fully-versioned row
    /// is a no-op. Mirrors <c>ProviderRepository.Hydrate</c>.
    ///
    /// </summary>
    internal static Claim Hydrate(Claim claim)
    {
        if (string.IsNullOrEmpty(claim.ClaimVersionId))
        {
            claim.ClaimVersionId = claim.Id;
        }
        if (claim.VersionNumber == 0)
        {
            claim.VersionNumber = 1;
        }
        if (claim.VersionState == ClaimVersionState.Unknown)
        {
            claim.VersionState = MapStatusToVersionState(claim.Status);
        }
        NormalizeAdjudicationProjection(claim);
        return claim;
    }

    internal static Claim NormalizeAdjudicationProjection(Claim claim)
    {
        if (!ShouldNormalizeFinancialSummary(claim.Status)
            || claim.ClaimLines.Count == 0)
        {
            return claim;
        }

        var lineAllowed = claim.ClaimLines.Sum(l => l.AdjudicationResult?.AllowedAmount ?? 0m);
        var linePaid = claim.ClaimLines.Sum(l => l.AdjudicationResult?.PaidAmount ?? 0m);
        var linePatientResponsibility = claim.ClaimLines.Sum(l => l.AdjudicationResult?.PatientResponsibility ?? 0m);

        if (lineAllowed == 0m && linePaid == 0m && linePatientResponsibility == 0m)
        {
            return claim;
        }

        claim.AdjudicationResult ??= new AdjudicationResult();
        if (HasEmptyFinancialSummary(claim.AdjudicationResult))
        {
            claim.AdjudicationResult.AllowedAmount = lineAllowed;
            claim.AdjudicationResult.PayerPayment = linePaid;
            claim.AdjudicationResult.PatientResponsibility = linePatientResponsibility;
            // Roll up the OOP-eligible amount only when every line carries
            // it; otherwise leave null so readers fall back to patient
            // responsibility.
            claim.AdjudicationResult.OopAppliedAmount =
                claim.ClaimLines.All(l => l.AdjudicationResult?.OopAppliedAmount is not null)
                    ? claim.ClaimLines.Sum(l => l.AdjudicationResult!.OopAppliedAmount!.Value)
                    : null;
        }

        claim.AdjudicationResult.DenialReasonCode = null;
        claim.AdjudicationResult.DenialReason = null;
        return claim;
    }

    private static bool ShouldNormalizeFinancialSummary(ClaimStatus status) => status switch
    {
        ClaimStatus.Approved or ClaimStatus.Paid or ClaimStatus.PartiallyPaid => true,
        _ => false
    };

    private static bool HasEmptyFinancialSummary(AdjudicationResult result) =>
        result.AllowedAmount == 0m
        && result.PayerPayment == 0m
        && result.PatientResponsibility == 0m
        && result.DeductibleAmount == 0m
        && result.CoinsuranceAmount == 0m
        && result.CopayAmount == 0m;

    /// <summary>
    /// Maps the legacy <see cref="ClaimStatus"/> operational signal onto a
    /// versioning <see cref="ClaimVersionState"/>. One canonical mapping for
    /// the repository and tests; the table is documented in
    /// docs/architecture/claim-versioning.md.
    /// </summary>
    public static ClaimVersionState MapStatusToVersionState(ClaimStatus status) => status switch
    {
        ClaimStatus.Submitted or
        ClaimStatus.Received or
        ClaimStatus.InAdjudication or
        ClaimStatus.Pended => ClaimVersionState.Submitted,
        // Pay and Deny: adjudicated, not yet finalized by the payment cycle
        // (non-terminal; reprocess / examiner resolve may still change them).
        ClaimStatus.Approved or
        ClaimStatus.Deny => ClaimVersionState.Adjudicated,
        ClaimStatus.Paid or
        ClaimStatus.PartiallyPaid => ClaimVersionState.Paid,
        // Denied is the FINALIZED denial (835 sent): terminal.
        ClaimStatus.Denied => ClaimVersionState.Denied,
        ClaimStatus.Voided => ClaimVersionState.Voided,
        _ => ClaimVersionState.Submitted
    };

    /// <summary>
    /// Claim dispositions that <see cref="IClaimRepository.UpdateAdjudicationProjectionAsync"/>'s
    /// <c>isPend: true</c> path must never downgrade back to
    /// <see cref="ClaimStatus.Pended"/> — see that method's doc comment for
    /// the full precedence rule. Shared with <see cref="ClaimRepositoryMongo"/>
    /// as one canonical rule, like <see cref="MapStatusToVersionState"/>.
    /// Pended is deliberately excluded: it is not a final disposition, so
    /// re-pending an already-Pended claim (a re-adjudication run refreshing
    /// PendDetails) is allowed.
    /// </summary>
    public static bool IsFinalDisposition(ClaimStatus status) => status switch
    {
        // Deny (like Approved) is a decided disposition: a stray write-back
        // must not re-litigate it — only an explicit examiner resolve /
        // reprocess (fenced on the resolution lock) changes it.
        ClaimStatus.Approved or
        ClaimStatus.Deny or
        ClaimStatus.Denied or
        ClaimStatus.Paid or
        ClaimStatus.PartiallyPaid or
        ClaimStatus.Voided => true,
        _ => false
    };

    /// <summary>
    /// Every status <see cref="IsFinalDisposition"/> returns true for. Derived,
    /// not hand-duplicated, so the Mongo <c>$nin</c> filter built from it can
    /// never drift from the canonical rule.
    /// </summary>
    public static readonly IReadOnlyList<ClaimStatus> FinalDispositions =
        Enum.GetValues<ClaimStatus>().Where(IsFinalDisposition).ToArray();

    /// <summary>
    /// True when <paramref name="status"/> must not be overwritten by a
    /// guarded status write — <c>TryTransitionStatusAsync</c> (backing
    /// <c>PUT /{id}/status</c>) and the adjudication pipeline's resolved-status
    /// projection in <c>UpdateAdjudicationProjectionAsync</c>.
    ///
    /// <para>
    /// Composes <see cref="IsFinalDisposition"/> (a stray re-adjudication
    /// write-back must not re-litigate an already-completed disposition) with
    /// <see cref="ClaimStatus.Pended"/> (a human-review gate — only an
    /// explicit, deliberate action, <c>POST work-queue/{id}/override</c>,
    /// resolves it; never a synchronous write-back that doesn't know the pend
    /// happened). This is deliberately a DIFFERENT set than
    /// <see cref="IsFinalDisposition"/> alone: that predicate excludes Pended
    /// on purpose, for the opposite write direction (the async orchestrator's
    /// own Pend projection in <c>UpdateAdjudicationProjectionAsync</c>, which
    /// must still be allowed to re-pend an already-Pended claim). Two
    /// directions of one precedence lattice, both anchored here so no call
    /// site forks its own copy. See docs/architecture/
    /// claim-adjudication-pipeline.md D9b for the full writer x precedence
    /// table.
    /// </para>
    /// </summary>
    public static bool BlocksSynchronousWriteback(ClaimStatus status) =>
        status == ClaimStatus.Pended || IsFinalDisposition(status);

    /// <summary>Derived set backing <see cref="BlocksSynchronousWriteback"/> — see that method's doc comment.</summary>
    public static readonly IReadOnlyList<ClaimStatus> SynchronousWritebackBlockedStatuses =
        Enum.GetValues<ClaimStatus>().Where(BlocksSynchronousWriteback).ToArray();

    /// <summary>
    /// True for the narrow repair allowed by the pipeline's resolved-status
    /// projection: a row already says Deny (the non-final adjudicated denial;
    /// a finalized Denied row is terminal and never patched), while the incoming result is paid and carries
    /// no denial evidence. This prevents an impossible "Denied + payer payment
    /// + no denial reason" state from surviving while still protecting pends
    /// and non-paid summaries.
    /// </summary>
    public static bool CanRepairContradictoryDeniedSummary(
        ClaimStatus preWriteStatus,
        ClaimStatus desiredStatus,
        AdjudicationResult incomingAdjudication) =>
        preWriteStatus == ClaimStatus.Deny
        && desiredStatus == ClaimStatus.Approved
        && incomingAdjudication.PayerPayment > 0
        && !HasDenialEvidence(incomingAdjudication);

    /// <summary>
    /// True for the inverse narrow repair allowed by the pipeline's
    /// resolved-status projection: a row already says Approved, while the incoming result is
    /// denied, pays nothing, and carries explicit denial evidence. This
    /// prevents an impossible "Approved + zero payer payment + denial reason"
    /// state without weakening pend protection or reopening a consistent
    /// final disposition.
    /// </summary>
    public static bool CanRepairContradictoryApprovedSummary(
        ClaimStatus preWriteStatus,
        ClaimStatus desiredStatus,
        AdjudicationResult incomingAdjudication) =>
        preWriteStatus == ClaimStatus.Approved
        && desiredStatus == ClaimStatus.Deny
        && incomingAdjudication.PayerPayment == 0
        && HasDenialEvidence(incomingAdjudication);

    /// <summary>
    /// True when an incoming adjudication result is strong enough to attempt
    /// the narrow live-row contradiction repair after a guarded status write
    /// was blocked. The live datastore predicate still decides whether a row
    /// is actually repairable, which lets this cover races where the status
    /// changed after the caller's pre-write snapshot.
    /// </summary>
    public static bool CanAttemptContradictoryStatusRepair(
        ClaimStatus desiredStatus,
        AdjudicationResult incomingAdjudication) =>
        (desiredStatus == ClaimStatus.Approved
            && incomingAdjudication.PayerPayment > 0
            && !HasDenialEvidence(incomingAdjudication))
        || (desiredStatus == ClaimStatus.Deny
            && incomingAdjudication.PayerPayment == 0
            && HasDenialEvidence(incomingAdjudication));

    private static bool HasDenialEvidence(AdjudicationResult? adjudication) =>
        adjudication is not null
        && (!string.IsNullOrWhiteSpace(adjudication.DenialReasonCode)
            || !string.IsNullOrWhiteSpace(adjudication.DenialReason)
            || adjudication.AdjustmentReasons?.Any() == true);

    /// <summary>
    /// A Denied claim with no payment-cycle evidence on claims-service's side:
    /// no check / 835 trace, no payment date, no outbound 835 control number,
    /// not superseded. Before <see cref="ClaimStatus.Deny"/> existed the
    /// pipeline set Denied at adjudication and the payment run never wrote
    /// back to a denial, so this does NOT prove the denial was never in an
    /// 835 — payment-service's envelopes are the only proof (see the
    /// backfill runbook); the backfill is opt-in, dry-run first, and takes
    /// the remitted claim ids to exclude.
    /// </summary>
    public static bool IsLegacyUnremittedDenied(Claim claim) =>
        claim.Status == ClaimStatus.Denied
        && claim.VersionState is ClaimVersionState.Denied or ClaimVersionState.Unknown
        && string.IsNullOrWhiteSpace(claim.AdjudicationResult?.CheckNumber)
        && claim.AdjudicationResult?.PaymentDate is null
        && string.IsNullOrWhiteSpace(claim.EDI835ControlNumber)
        && claim.SupersededAt is null;

    /// <summary>
    /// The live-row check of the narrow contradiction repair (formerly two
    /// patch filter predicates): Deny + paid + no denial code → Approved;
    /// Approved + unpaid + denial evidence (code, reason or adjustment
    /// reasons) → Deny. A finalized Denied row is never repaired. With a resolution lock token, the row must still
    /// hold that lock.
    /// </summary>
    internal static bool IsRepairableContradiction(Claim row, ClaimStatus desiredStatus, string? requiredResolutionLockToken)
    {
        if (requiredResolutionLockToken is not null
            && !string.Equals(row.ResolutionLock?.Token, requiredResolutionLockToken, StringComparison.Ordinal))
            return false;
        var adjudication = row.AdjudicationResult;
        if (adjudication is null) return false;
        return desiredStatus switch
        {
            ClaimStatus.Approved => row.Status == ClaimStatus.Deny
                                    && adjudication.PayerPayment > 0
                                    && string.IsNullOrEmpty(adjudication.DenialReasonCode),
            ClaimStatus.Deny => row.Status == ClaimStatus.Approved
                                  && adjudication.PayerPayment == 0
                                  && HasDenialEvidence(adjudication),
            _ => false,
        };
    }

    /// <summary>
    /// Upper bound on rows <see cref="IClaimRepository.FindDuplicateCandidatesAsync"/> returns.
    /// A member's live claims overlapping one service period are normally a
    /// handful; the cap keeps a pathological member (or a bad date range)
    /// from turning one adjudication into an unbounded scan.
    /// </summary>
    public const int MaxDuplicateCandidates = 200;
}
