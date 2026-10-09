using AccumulatorService.Models;
using CloudHealthOffice.Events;

namespace AccumulatorService.Services;

/// <summary>
/// Apply result for ClaimFinalized processing. Encodes the three possible
/// outcomes — applied, duplicate (idempotent skip), orphan (no matching plan-year
/// snapshot could be resolved) — without exceptions, so the Kafka consumer can
/// log/metric each case.
/// </summary>
public enum ApplyOutcome
{
    Applied,
    Duplicate,
    Orphan,

    /// <summary>
    /// Another attempt for the same claim (or reversal) is in flight: nothing
    /// was written. Not terminal — the consumer leaves the offset uncommitted
    /// and the message is retried.
    /// </summary>
    InProgress,

    /// <summary>Not applied by design (a denied claim): terminal, nothing written.</summary>
    Skipped
}

public record ApplyResult(ApplyOutcome Outcome, AccumulatorSnapshot? Snapshot, string? EventId, string? Reason);

public interface IAccumulatorService
{
    Task<AccumulatorResponse?> GetAsync(string tenantId, string memberId, DateTime? asOfDate, CancellationToken ct = default);

    Task<AccumulatorHistoryResponse> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default);

    Task<ApplyResult> ApplyClaimFinalizedAsync(ClaimFinalizedEvent evt, CancellationToken ct = default);

    /// <param name="actorId">The authenticated user performing the adjustment (from the token).</param>
    Task<AccumulatorAdjustmentResponse> AdjustAsync(string tenantId, string memberId, string actorId, AccumulatorAdjustmentRequest request, CancellationToken ct = default);
}
