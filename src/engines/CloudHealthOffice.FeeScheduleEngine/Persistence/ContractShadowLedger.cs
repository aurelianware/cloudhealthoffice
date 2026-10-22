using CloudHealthOffice.FeeScheduleEngine.Configuration;
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

    /// <summary>
    /// One contract lookup: what each store answered with, and which of the two
    /// the claim is being paid on.
    /// </summary>
    public sealed record Entry(ProviderContract? Legacy, ProviderContract? Canonical, ContractPricingMode Mode)
    {
        /// <summary>The contract the paid pass used.</summary>
        public ProviderContract? Paid => Mode == ContractPricingMode.Canonical ? Canonical ?? Legacy : Legacy;

        /// <summary>The contract the shadow pass prices with: the other one.</summary>
        public ProviderContract? Shadow => Mode == ContractPricingMode.Canonical ? Legacy : Canonical;
    }

    /// <summary>True when at least one lookup found a canonical contract to shadow against.</summary>
    public bool HasCanonicalContract => _entries.Values.Any(e => e.Canonical is not null);

    /// <summary>The lookups recorded so far, for a parity record that has to name them.</summary>
    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    public static string KeyFor(string tenantId, string providerNpi, string planId, DateTime serviceDate)
        => $"{tenantId}|{providerNpi}|{planId}|{serviceDate:yyyyMMdd}";

    public void Clear() => _entries.Clear();

    /// <summary>
    /// Which contract this claim is paid on. A claim is one tenant's, so one
    /// mode; <see cref="ContractPricingMode.Off"/> when nothing was recorded.
    /// </summary>
    public ContractPricingMode Mode => _entries.Count == 0 ? ContractPricingMode.Off : _entries.Values.First().Mode;

    public void Record(
        string key, ProviderContract? legacy, ProviderContract? canonical, ContractPricingMode mode)
        => _entries[key] = new Entry(legacy, canonical, mode);

    public bool TryGet(string key, out Entry entry) => _entries.TryGetValue(key, out entry!);
}

/// <summary>
/// The contract repository the shadow pass reads: whichever contract the paid
/// pass did <b>not</b> use, out of <see cref="ContractShadowLedger"/>. In the
/// phase 4 window that is the canonical contract; after the phase 5 cutover it is
/// the legacy one, so the window keeps running in the other direction for a cycle.
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
        return Task.FromResult(_ledger.TryGet(key, out var entry) ? entry.Shadow : null);
    }

    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
        => throw new NotSupportedException("The shadow pass prices; it never writes.");

    public Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
        => throw new NotSupportedException("The shadow pass prices one claim; it lists nothing.");
}
