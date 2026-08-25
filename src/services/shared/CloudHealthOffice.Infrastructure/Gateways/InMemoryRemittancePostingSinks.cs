using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Process-local claim posting sink for tests and Development. Records
/// financial effect without calling claims-service or inventing 835s.
/// </summary>
public sealed class InMemoryClaimRemittancePostingSink : IClaimRemittancePostingSink
{
    private readonly ConcurrentDictionary<string, RemittanceClaimPost> _posted =
        new(StringComparer.Ordinal);

    public IReadOnlyList<RemittanceClaimPost> Posted => _posted.Values.ToList();

    public Task<RemittanceClaimPostResult> PostAsync(
        RemittanceClaimPost request,
        CancellationToken cancellationToken = default)
    {
        var key = $"{request.TenantId}|{request.ClaimId}|{request.RemittanceId}";
        if (!_posted.TryAdd(key, request))
        {
            return Task.FromResult(new RemittanceClaimPostResult(RemittanceClaimPostOutcome.AlreadyPosted));
        }

        return Task.FromResult(new RemittanceClaimPostResult(RemittanceClaimPostOutcome.Posted));
    }
}

/// <summary>
/// Process-local accumulator sink for tests and Development. Idempotent on
/// remittance+claim. Does not call accumulator-service.
/// </summary>
public sealed class InMemoryRemittanceAccumulatorSink : IRemittanceAccumulatorSink
{
    private readonly ConcurrentDictionary<string, RemittanceAccumulatorApply> _applied =
        new(StringComparer.Ordinal);

    public IReadOnlyList<RemittanceAccumulatorApply> Applied => _applied.Values.ToList();

    public Task<RemittanceAccumulatorApplyResult> ApplyAsync(
        RemittanceAccumulatorApply request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.MemberId))
        {
            return Task.FromResult(new RemittanceAccumulatorApplyResult(
                RemittanceAccumulatorApplyOutcome.Skipped, "missing-member"));
        }

        var key = $"{request.TenantId}|{request.RemittanceId}|{request.ClaimId}";
        if (!_applied.TryAdd(key, request))
        {
            return Task.FromResult(new RemittanceAccumulatorApplyResult(
                RemittanceAccumulatorApplyOutcome.Duplicate));
        }

        return Task.FromResult(new RemittanceAccumulatorApplyResult(
            RemittanceAccumulatorApplyOutcome.Applied));
    }
}
