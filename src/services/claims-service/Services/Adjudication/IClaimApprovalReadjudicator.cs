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

    public string? CorrelationId { get; init; }

    /// <summary>
    /// The payer order the examiner confirmed for a claim pended on
    /// coordination of benefits: 1 = this plan is primary, 2 = secondary, 3+
    /// = tertiary or later. Required to approve a COB pend — a payer-order
    /// mismatch, duplicate sequences, unplaceable line adjudication or
    /// missing prior-payer data is never paid as primary by a plain approval.
    /// With 2 or more the 837 must carry the prior payers' data (2320 / 2430)
    /// for every earlier sequence.
    /// </summary>
    public int? PayerSequence { get; init; }
}

/// <summary>Outcome of re-adjudicating a pended claim for an examiner's approval.</summary>
public sealed record ApprovalReadjudicationResult(
    ClaimAdjudicationOutcome Outcome,
    string? Reason,
    IReadOnlyList<string>? UnresolvedReasons = null);

/// <summary>
/// Re-runs adjudication for an examiner-approved pended claim, in Production,
/// with the examiner's decision applied: review pends (possible duplicate,
/// provider integrity, network, NCCI, AI) are cleared, a COB pend is resolved
/// by <see cref="ExaminerApproval.PayerSequence"/>. The payment and the
/// accumulator writes then come from a Production pass — a pended claim is
/// priced read-only and never wrote any.
/// </summary>
public interface IClaimApprovalReadjudicator
{
    Task<ApprovalReadjudicationResult> ReadjudicateForApprovalAsync(
        string tenantId, string claimId, ExaminerApproval approval, CancellationToken ct);
}
