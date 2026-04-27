using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IProviderVerificationClient"/>. Tests register
/// canned <see cref="VerificationResult"/>s by NPI; calls to
/// <see cref="VerifyBatchAsync"/> return the matching subset and record
/// every batch invocation for later assertion.
/// </summary>
public sealed class FakeProviderVerificationClient : IProviderVerificationClient
{
    public Dictionary<string, VerificationResult> Canned { get; } = new();
    public List<IReadOnlyList<string>> Calls { get; } = new();

    /// <summary>
    /// When true, <see cref="VerifyBatchAsync"/> returns no records (a
    /// verification-source outage). Cached scores stay put.
    /// </summary>
    public bool SimulateOutage { get; set; }

    public Task<IReadOnlyList<VerificationResult>> VerifyBatchAsync(
        IReadOnlyList<string> npis, CancellationToken ct = default)
    {
        Calls.Add(npis.ToList());
        if (SimulateOutage)
        {
            return Task.FromResult<IReadOnlyList<VerificationResult>>(
                Array.Empty<VerificationResult>());
        }

        var results = npis
            .Where(n => Canned.ContainsKey(n))
            .Select(n => Canned[n])
            .ToList();
        return Task.FromResult<IReadOnlyList<VerificationResult>>(results);
    }

    public void Seed(string npi, int score, string rating)
    {
        Canned[npi] = new VerificationResult
        {
            Npi = npi,
            Status = VerificationOutcome.Verified,
            IntegrityScore = new CompositeIntegrityScore
            {
                CompositeScore = score,
                Rating = rating,
            },
            LastVerifiedAt = DateTimeOffset.UtcNow,
        };
    }
}
