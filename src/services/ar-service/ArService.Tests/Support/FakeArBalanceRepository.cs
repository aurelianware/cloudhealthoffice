using System.Text.Json;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Support;

/// <summary>
/// In-memory AR balances that behave like the Mongo repository: reads return
/// copies, and a save of a stale copy (older <see cref="ArBalance.Version"/>)
/// throws <see cref="ArConcurrencyException"/>. With
/// <see cref="AutoCreateGlAccountId"/> set, a missing balance that is asked for
/// is created on that GL account (for tests about the posting, not the balance).
/// </summary>
public sealed class FakeArBalanceRepository : IArBalanceRepository
{
    private readonly Dictionary<string, ArBalance> _stored = new();

    public string? AutoCreateGlAccountId { get; init; }
    public int Updates { get; private set; }

    /// <summary>Runs before each save; lets a test change the stored balance as another writer would.</summary>
    public Action<FakeArBalanceRepository, ArBalance>? BeforeUpdate { get; set; }

    public static FakeArBalanceRepository AutoCreating(string glAccountId) => new() { AutoCreateGlAccountId = glAccountId };

    /// <summary>The saved balance (a copy).</summary>
    public ArBalance this[string id] => Clone(_stored[id]);

    public bool Contains(string id) => _stored.ContainsKey(id);

    public void Add(string id, string glAccountId, decimal openingBalance = 0m)
    {
        _stored[id] = new ArBalance
        {
            Id = id,
            TenantId = "tenant-1",
            GlAccountId = glAccountId,
            AccountNumber = "1200",
            Period = new DateTime(2026, 3, 1),
            OpeningBalance = openingBalance,
            ClosingBalance = openingBalance,
            SponsorBalance = openingBalance
        };
    }

    /// <summary>Saves a change as a concurrent writer would (bumps the version).</summary>
    public void ChangeStored(string id, Action<ArBalance> change)
    {
        var balance = _stored[id];
        change(balance);
        balance.Version++;
    }

    public Task<ArBalance?> GetByIdAsync(string id)
    {
        if (!_stored.ContainsKey(id) && AutoCreateGlAccountId != null)
            Add(id, AutoCreateGlAccountId);
        return Task.FromResult(_stored.TryGetValue(id, out var balance) ? Clone(balance) : null);
    }

    public Task<ArBalance> UpdateAsync(ArBalance balance)
    {
        BeforeUpdate?.Invoke(this, balance);
        if (_stored.TryGetValue(balance.Id, out var current) && current.Version != balance.Version)
            throw new ArConcurrencyException($"AR balance {balance.Id} changed");
        Updates++;
        balance.Version++;
        _stored[balance.Id] = Clone(balance);
        return Task.FromResult(balance);
    }

    public Task<IEnumerable<ArBalance>> SearchAsync(string? accountId = null, DateTime? period = null, bool? isReconciled = null, int page = 1, int pageSize = 50)
        => Task.FromResult<IEnumerable<ArBalance>>(_stored.Values.Select(Clone).ToList());

    public Task<IEnumerable<ArBalance>> GetByAccountIdAsync(string accountId)
        => Task.FromResult<IEnumerable<ArBalance>>(_stored.Values.Where(b => b.GlAccountId == accountId).Select(Clone).ToList());

    public Task<ArBalance> CreateAsync(ArBalance balance)
    {
        _stored[balance.Id] = Clone(balance);
        return Task.FromResult(balance);
    }

    public Task<IEnumerable<ArBalance>> GetBalancesContainingMemberAsync(string memberId)
        => Task.FromResult<IEnumerable<ArBalance>>(_stored.Values.Where(b => b.PostingEntries.Any(e => e.MemberId == memberId)).Select(Clone).ToList());

    private static ArBalance Clone(ArBalance balance) =>
        JsonSerializer.Deserialize<ArBalance>(JsonSerializer.Serialize(balance))!;
}
