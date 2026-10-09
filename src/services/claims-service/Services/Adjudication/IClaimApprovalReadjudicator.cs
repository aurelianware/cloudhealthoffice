using ClaimsService.Models;
using ClaimsService.Models.Adjudication;

namespace ClaimsService.Services.Adjudication;

/// <summary>
/// An examiner's approval of a pended claim, applied to a re-run of the
/// adjudication pipeline (<see cref="IClaimApprovalReadjudicator"/>).
/// </summary>
public sealed record ExaminerApproval
{
    /// <summary>The examiner (authenticated caller).</summary>
    public string? ExaminerId { get; init; }

    /// <summary>Identifies this approval attempt (the re-run's Service Bus MessageId).</summary>
    public string ApprovalId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// A second, different approver who signed off (required to price a
    /// claim as primary when a prior payer paid on it). Null when none.
    /// </summary>
    public string? SecondApproverId { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>The examiner's reason (required for a payer-order override).</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The payer order the examiner confirmed for a claim pended on
    /// coordination of benefits: 1 = this plan is primary, 2 = secondary, 3+
    /// = tertiary or later, never more than the payers on the claim. Accepted
    /// only for a COB pend (the API returns 400 otherwise). With 2 or more
    /// the 837 must carry the prior payers' data (2320 / 2430) for every
    /// earlier sequence.
    /// </summary>
    public int? PayerSequence { get; init; }

    /// <summary>
    /// The approver holds <c>claims:override-approve</c> and gave a reason:
    /// required when <see cref="PayerSequence"/> disagrees with
    /// coverage-service or the 837's SBR01.
    /// </summary>
    public bool PayerOrderOverrideAuthorized { get; init; }

    /// <summary>
    /// The claim's persisted pend — what the examiner actually reviewed.
    /// Only pends matching it are overridden on the re-run (PR #1278 round
    /// 3, B1): a pend the examiner never saw — a provider-integrity check
    /// that is unreachable now, a newly found duplicate, a retro plan
    /// change — keeps the claim pended and the approval is refused.
    /// </summary>
    public PendDetails? ReviewedPend { get; init; }

    /// <summary>The reviewed pends as (code, reason), the routing one first.</summary>
    public IReadOnlyList<(string Code, string? Reason)> ReviewedPends => ReviewedFrom(ReviewedPend);

    public static IReadOnlyList<(string Code, string? Reason)> ReviewedFrom(PendDetails? pend)
    {
        var list = new List<(string, string?)>();
        if (pend is null) return list;
        if (!string.IsNullOrWhiteSpace(pend.PendCode))
            list.Add((pend.PendCode.Trim(), pend.PendReason));
        foreach (var entry in pend.AdditionalPendReasons ?? [])
        {
            var (code, reason) = SplitEntry(entry);
            if (code is not null) list.Add((code, reason));
        }
        return list;
    }

    /// <summary>"{code}: {reason}" → (code, reason); (null, entry) when it has no code.</summary>
    public static (string? Code, string? Reason) SplitEntry(string entry)
    {
        var colon = entry.IndexOf(':');
        if (colon <= 0) return (null, entry);
        var code = entry[..colon].Trim();
        return code.Length is > 0 and <= 20 && !code.Contains(' ')
            ? (code, entry[(colon + 1)..].Trim())
            : (null, entry);
    }

    /// <summary>
    /// Whether the examiner reviewed a pend with <paramref name="code"/>
    /// (and, when <paramref name="exactReason"/>, exactly this
    /// <paramref name="reason"/> — the rule for pends whose reason is the
    /// finding itself: provider integrity, duplicates, retro plan change,
    /// subrogation / TPL, spend-down).
    /// </summary>
    public bool Reviewed(string code, string? reason, bool exactReason) =>
        ReviewedPends.Any(r =>
            string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)
            && (!exactReason || SameReason(r.Reason, reason)));

    /// <summary>
    /// Equal reasons — or a stored reason cut at the 500-character limit
    /// (an additional "{code}: {reason}" entry loses the code's length) that
    /// the candidate begins with.
    /// </summary>
    private static bool SameReason(string? stored, string? candidate)
    {
        var s = Normalize(stored);
        var c = Normalize(candidate);
        return string.Equals(s, c, StringComparison.Ordinal)
               || (s.Length >= 470 && c.StartsWith(s, StringComparison.Ordinal));
    }

    /// <summary>Pend reasons are stored truncated to 500 characters.</summary>
    private static string Normalize(string? reason)
    {
        var r = (reason ?? string.Empty).Trim();
        return r.Length > 500 ? r[..500] : r;
    }
}

/// <summary>Outcome of re-adjudicating a pended claim for an examiner's approval.</summary>
public sealed record ApprovalReadjudicationResult(
    ClaimAdjudicationOutcome Outcome,
    string? Reason,
    IReadOnlyList<string>? UnresolvedReasons = null)
{
    /// <summary>The pends the re-run overrode ("{stage}: {code}: {reason}"), for the audit record.</summary>
    public IReadOnlyList<string> OverriddenPends { get; init; } = [];
}

/// <summary>
/// Re-runs adjudication for an examiner-approved pended claim, in Production,
/// with the examiner's decision applied: only the pends the examiner
/// reviewed (<see cref="ExaminerApproval.ReviewedPend"/>) are cleared, a COB
/// pend is resolved by <see cref="ExaminerApproval.PayerSequence"/>. The
/// payment and the accumulator writes then come from a Production pass — a
/// claim pended before benefit calculation is priced read-only and never
/// wrote any.
/// </summary>
public interface IClaimApprovalReadjudicator
{
    Task<ApprovalReadjudicationResult> ReadjudicateForApprovalAsync(
        string tenantId, string claimId, ExaminerApproval approval, CancellationToken ct);
}
