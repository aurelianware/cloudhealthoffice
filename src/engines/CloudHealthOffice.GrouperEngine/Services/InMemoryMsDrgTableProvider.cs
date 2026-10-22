using CloudHealthOffice.GrouperEngine.Models;

namespace CloudHealthOffice.GrouperEngine.Services;

/// <summary>
/// Serves a fixed set of grouper versions held in memory. Used by the tests,
/// by the development seed, and as the cache layer a Mongo-backed provider
/// will fill.
/// </summary>
public sealed class InMemoryMsDrgTableProvider : IMsDrgTableProvider
{
    private readonly IReadOnlyList<MsDrgTableSet> _versions;

    public InMemoryMsDrgTableProvider(params MsDrgTableSet[] versions)
        : this((IEnumerable<MsDrgTableSet>)versions)
    {
    }

    public InMemoryMsDrgTableProvider(IEnumerable<MsDrgTableSet> versions)
    {
        // Newest first, so a date covered by two overlapping windows takes the
        // later version — the same precedence a quarterly reload gives.
        _versions = versions.OrderByDescending(v => v.EffectiveFrom).ToList();
    }

    public Task<MsDrgTableSet?> GetForDischargeDateAsync(
        DateOnly dischargeDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(_versions.FirstOrDefault(v => v.Covers(dischargeDate)));
}
