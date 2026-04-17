using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Thin client for the future accumulator-service. Today only a stub
/// implementation exists; the real service is scoped to a later prompt.
/// Kept on an interface so consumers (TemporalEligibilityService) don't
/// depend on the stub and can be swapped without further changes.
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
