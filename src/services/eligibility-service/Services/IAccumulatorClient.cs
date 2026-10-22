using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Date-bound accumulator query boundary. A response must distinguish an
/// unavailable historical snapshot from a measured zero. The existing
/// accumulator-service plan-year lookup is not a point-in-time source.
/// </summary>
public interface IAccumulatorClient
{
    Task<AccumulatorSnapshot> GetSnapshotAsync(
        string tenantId,
        string memberId,
        string planId,
        DateTime asOfDate,
        CancellationToken ct = default);
}

/// <summary>
/// Placeholder implementation that returns zeroed accumulator values and
/// marks <see cref="AccumulatorSnapshot.Source"/> as "stub" so callers
/// (and UI) can distinguish stubbed snapshots from live data.
/// </summary>
public class StubAccumulatorClient : IAccumulatorClient
{
    public Task<AccumulatorSnapshot> GetSnapshotAsync(
        string tenantId,
        string memberId,
        string planId,
        DateTime asOfDate,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AccumulatorSnapshot
        {
            Source = "stub",
            AsOfDate = asOfDate,
            Deductible = new DeductibleInfo(),
            OutOfPocket = new OutOfPocketInfo()
        });
    }
}

/// <summary>No historical source is configured; never invent amounts.</summary>
public sealed class UnavailableAccumulatorClient : IAccumulatorClient
{
    public Task<AccumulatorSnapshot> GetSnapshotAsync(string tenantId, string memberId,
        string planId, DateTime asOfDate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new AccumulatorSnapshot
        {
            Source = "unavailable",
            AsOfDate = asOfDate,
            UnavailableReason = "A plan-specific point-in-time accumulator source is not implemented.",
            Deductible = null,
            OutOfPocket = null,
        });
    }
}
