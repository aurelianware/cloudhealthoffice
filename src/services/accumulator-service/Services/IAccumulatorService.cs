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
    Orphan
}

public record ApplyResult(ApplyOutcome Outcome, AccumulatorSnapshot? Snapshot, string? EventId, string? Reason);

public interface IAccumulatorService
{
    Task<AccumulatorResponse?> GetAsync(string tenantId, string memberId, DateTime? asOfDate, CancellationToken ct = default);

    Task<AccumulatorHistoryResponse> GetHistoryAsync(string tenantId, string memberId, CancellationToken ct = default);

    Task<ApplyResult> ApplyClaimFinalizedAsync(ClaimFinalizedEvent evt, CancellationToken ct = default);

    Task<AccumulatorAdjustmentResponse> AdjustAsync(string tenantId, string memberId, AccumulatorAdjustmentRequest request, CancellationToken ct = default);
}
