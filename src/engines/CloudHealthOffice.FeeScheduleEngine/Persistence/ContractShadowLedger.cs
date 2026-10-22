using CloudHealthOffice.FeeScheduleEngine.Models;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// What the dual read of ADR 016 phase 4 found, for the claim being priced.
///
/// <para>
/// The engine looks a contract up once per line, by tenant, provider, plan and
/// service date. The dual read answers every one of those lookups with the
/// legacy contract — the claim is paid on legacy for the whole window — and
/// records the canonical contract it found beside it here. The shadow pass then
/// prices the same lines again out of this ledger, so the second pass reads no
/// database of its own and cannot see a store that moved underneath it.
/// </para>
///
/// <para>
/// Scoped to the unit of work that prices one claim. <see cref="Clear"/> runs
/// before each paid pass, so one claim's lookups are never shadowed against
/// another's.
/// </para>
/// </summary>
public sealed class ContractShadowLedger
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>One contract lookup: what each store answered with.</summary>
    public sealed record Entry(ProviderContract? Legacy, ProviderContract? Canonical);

    /// <summary>True when at least one lookup found a canonical contract to shadow against.</summary>
    public bool HasCanonicalContract => _entries.Values.Any(e => e.Canonical is not null);

    /// <summary>The lookups recorded so far, for a parity record that has to name them.</summary>
    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    public static string KeyFor(string tenantId, string providerNpi, string planId, DateTime serviceDate)
        => $"{tenantId}|{providerNpi}|{planId}|{serviceDate:yyyyMMdd}";

    public void Clear() => _entries.Clear();

    public void Record(string key, ProviderContract? legacy, ProviderContract? canonical)
        => _entries[key] = new Entry(legacy, canonical);

    public bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry!);
}

/// <summary>
/// The contract repository the shadow pass reads: the canonical contract the
/// dual read already found, out of <see cref="ContractShadowLedger"/>.
///
/// <para>
/// It touches no database. A lookup the paid pass never made has no entry and
/// answers null, which prices as the plan default — the same answer a provider
/// with no contract gets, so a parity delta on such a line is a real finding
/// rather than an artefact of the shadow pass reading something else.
/// </para>
/// </summary>
public sealed class ShadowContractRepository : IProviderContractRepository
{
    private readonly ContractShadowLedger _ledger;

    public ShadowContractRepository(ContractShadowLedger ledger) => _ledger = ledger;

    public Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate,
        CancellationToken ct = default)
    {
        var key = ContractShadowLedger.KeyFor(tenantId, providerNpi, planId, serviceDate);
        return Task.FromResult(_ledger.TryGet(key, out var entry) ? entry.Canonical : null);
    }

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
        => throw new NotSupportedException("The shadow pass prices; it never writes.");

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
        => throw new NotSupportedException("The shadow pass prices one claim; it lists nothing.");
}
