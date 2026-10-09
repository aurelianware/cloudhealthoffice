using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Support;

/// <summary>
/// In-memory AR balances. With <see cref="AutoCreateGlAccountId"/> set, a
/// balance that is asked for and missing is created on that GL account
/// (for tests that are about the posting, not the balance).
/// </summary>
public sealed class FakeArBalanceRepository : IArBalanceRepository
{
    public Dictionary<string, ArBalance> Balances { get; } = new();
    public string? AutoCreateGlAccountId { get; init; }
    public int Updates { get; private set; }

    public static FakeArBalanceRepository AutoCreating(string glAccountId) => new() { AutoCreateGlAccountId = glAccountId };

    public ArBalance Add(string id, string glAccountId, decimal openingBalance = 0m)
    {
        var balance = new ArBalance
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
        Balances[id] = balance;
        return balance;
    }

    public Task<ArBalance?> GetByIdAsync(string id)
    {
        if (!Balances.TryGetValue(id, out var balance) && AutoCreateGlAccountId != null)
            balance = Add(id, AutoCreateGlAccountId);
        return Task.FromResult(balance);
    }

    public Task<ArBalance> UpdateAsync(ArBalance balance)
    {
        Updates++;
        Balances[balance.Id] = balance;
        return Task.FromResult(balance);
    }

    public Task<IEnumerable<ArBalance>> SearchAsync(string? accountId = null, DateTime? period = null, bool? isReconciled = null, int page = 1, int pageSize = 50)
        => Task.FromResult<IEnumerable<ArBalance>>(Balances.Values.ToList());

    public Task<IEnumerable<ArBalance>> GetByAccountIdAsync(string accountId)
        => Task.FromResult<IEnumerable<ArBalance>>(Balances.Values.Where(b => b.GlAccountId == accountId).ToList());

    public Task<ArBalance> CreateAsync(ArBalance balance)
    {
        Balances[balance.Id] = balance;
        return Task.FromResult(balance);
    }

    public Task<IEnumerable<ArBalance>> GetBalancesContainingMemberAsync(string memberId)
        => Task.FromResult<IEnumerable<ArBalance>>(Balances.Values.Where(b => b.PostingEntries.Any(e => e.MemberId == memberId)).ToList());
}
