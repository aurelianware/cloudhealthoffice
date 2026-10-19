namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Work Queue
// ---------------------------------------------------------------------------

public interface IWorkQueueService
{
    Task<WorkQueueSummary> GetQueueSummaryAsync();
    Task<List<WorkQueueItem>> GetQueueItemsAsync(string? queueType = null,
        string? assignedTo = null, int limit = 100);
    Task AssignClaimAsync(string claimId, string assignTo);
    /// <summary>
    /// Supervisor override (approval). <paramref name="payerSequence"/> is the
    /// payer order confirmed for a COB pend. Throws
    /// <see cref="ClaimResolutionRefusedException"/> with the service's reason
    /// when it refuses (400 / 403 / 409).
    /// </summary>
    Task<ClaimResolutionResult> OverrideAsync(string claimId, string overrideReason, int? payerSequence = null,
        string? pendFingerprint = null);

    /// <summary>
    /// Approve or deny a pended claim. <paramref name="payerSequence"/> is the
    /// payer order confirmed for a COB pend. A 202 (waiting for a second
    /// approver) returns <see cref="ClaimResolutionResult.AwaitingSecondApproval"/>;
    /// a refusal throws <see cref="ClaimResolutionRefusedException"/>.
    /// </summary>
    Task<ClaimResolutionResult> ResolvePendedClaimAsync(
        string claimId,
        string disposition,
        string reason,
        string? aiExaminerAgreement,
        string examinerUserId,
        int? payerSequence = null,
        string? pendFingerprint = null,
        IReadOnlyList<EditDecisionInput>? editDecisions = null,
        IReadOnlyList<LineDenialInput>? lineDenials = null);
}
