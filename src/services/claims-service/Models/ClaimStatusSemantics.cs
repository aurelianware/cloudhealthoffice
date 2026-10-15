namespace ClaimsService.Models;

/// <summary>
/// The claim lifecycle phases in one place (modeled on QNXT PAY/PAID,
/// DENY/DENIED; see docs/architecture/claim-versioning.md). Call sites ask
/// one of these questions instead of comparing to individual statuses, so a
/// new status (e.g. <see cref="ClaimStatus.Deny"/>) is classified once.
///
/// <list type="table">
///   <listheader><term>Phase</term><description>Statuses / editable?</description></listheader>
///   <item><term>Adjudicated, not yet remitted</term><description>
///     Pended, Approved ("Pay"), Deny — editable: reprocess, examiner
///     resolve / override.</description></item>
///   <item><term>Finalized by the payment cycle (835 sent)</term><description>
///     Paid, Denied (and legacy PartiallyPaid) — not editable: only an
///     adjustment (new version) or a void / reversal.</description></item>
///   <item><term>Corrected / reversed</term><description>
///     Voided (and VersionState Adjusted) — not editable.</description></item>
/// </list>
/// </summary>
public static class ClaimStatusSemantics
{
    /// <summary>Denied in either phase: <see cref="ClaimStatus.Deny"/> (awaiting the 835) or <see cref="ClaimStatus.Denied"/> (finalized).</summary>
    public static bool IsDenied(ClaimStatus status) =>
        status is ClaimStatus.Deny or ClaimStatus.Denied;

    /// <summary>
    /// Adjudicated payable and awaiting the payment run's finalize:
    /// <see cref="ClaimStatus.Approved"/> ("Pay"), plus legacy
    /// <see cref="ClaimStatus.PartiallyPaid"/> (finalize accepted it before,
    /// and still does).
    /// </summary>
    public static bool IsPayable(ClaimStatus status) =>
        status is ClaimStatus.Approved or ClaimStatus.PartiallyPaid;

    /// <summary>Paid in either phase as a claim-level outcome (payable or paid): Approved, Paid, legacy PartiallyPaid.</summary>
    public static bool IsPayOutcome(ClaimStatus status) =>
        status is ClaimStatus.Approved or ClaimStatus.Paid or ClaimStatus.PartiallyPaid;

    /// <summary>
    /// The adjudication decided the claim (pay or deny) but the payment cycle
    /// has not finalized it: <see cref="ClaimStatus.Approved"/> or
    /// <see cref="ClaimStatus.Deny"/>. Still editable.
    /// </summary>
    public static bool IsAdjudicatedNotFinalized(ClaimStatus status) =>
        status is ClaimStatus.Approved or ClaimStatus.Deny;

    /// <summary>
    /// Finalized by the payment cycle, or reversed: <see cref="ClaimStatus.Paid"/>,
    /// <see cref="ClaimStatus.Denied"/>, legacy <see cref="ClaimStatus.PartiallyPaid"/>,
    /// <see cref="ClaimStatus.Voided"/>. Only an adjustment or a void changes it.
    /// </summary>
    public static bool IsFinalized(ClaimStatus status) =>
        status is ClaimStatus.Paid or ClaimStatus.Denied or ClaimStatus.PartiallyPaid or ClaimStatus.Voided;

    /// <summary>
    /// Finalized (see <see cref="IsFinalized(ClaimStatus)"/>) or a terminal
    /// version state (Paid, Denied, Adjusted, Voided) — the row a resolve or
    /// reprocess must refuse.
    /// </summary>
    public static bool IsFinalized(Claim claim) =>
        IsFinalized(claim.Status)
        || claim.VersionState is ClaimVersionState.Paid or ClaimVersionState.Denied
            or ClaimVersionState.Adjusted or ClaimVersionState.Voided;

    /// <summary>
    /// A claim an examiner may resolve / override or reprocess (re-run the
    /// pipeline): Pended, or adjudicated and not yet finalized (Pay / Deny).
    /// </summary>
    public static bool IsReprocessable(ClaimStatus status) =>
        status == ClaimStatus.Pended || IsAdjudicatedNotFinalized(status);

    /// <summary>Every status <see cref="IsReprocessable"/> accepts (store predicates are built from it).</summary>
    public static readonly IReadOnlyList<ClaimStatus> ReprocessableStatuses =
        Enum.GetValues<ClaimStatus>().Where(IsReprocessable).ToArray();

    /// <summary>Every status <see cref="IsDenied"/> accepts.</summary>
    public static readonly IReadOnlyList<ClaimStatus> DeniedStatuses =
        Enum.GetValues<ClaimStatus>().Where(IsDenied).ToArray();

    /// <summary>The 422 detail for a resolve / reprocess of a finalized claim.</summary>
    public static string FinalizedRefusal(Claim claim) =>
        $"Claim {claim.Id} is {DisplayLabel(claim.Status)} (VersionState {claim.VersionState}): it was finalized by the " +
        "payment cycle (835 sent) or reversed, so it cannot be reprocessed or resolved. Correct it with an adjustment " +
        "(POST /api/v1/claims/{id}/adjustments, a new version) or, if it was paid, reverse it (POST /api/claims/{id}/void).";

    /// <summary>The business label for a status ("Pay" for Approved, "Paid with line denials" is a portal-side derivation).</summary>
    public static string DisplayLabel(ClaimStatus status) => status switch
    {
        ClaimStatus.Approved => "Pay",
        ClaimStatus.Deny => "Deny",
        ClaimStatus.InAdjudication => "In Adjudication",
        ClaimStatus.PartiallyPaid => "Paid",
        _ => status.ToString(),
    };
}
