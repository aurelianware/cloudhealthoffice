using CloudHealthOffice.FeeScheduleEngine.Configuration;
using CloudHealthOffice.FeeScheduleEngine.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.FeeScheduleEngine.Persistence;

/// <summary>
/// The dual read of ADR 016 phase 4: every contract lookup reads both the
/// legacy engine contract and the canonical contract the backfill wrote.
///
/// <para>
/// <b>It answers with legacy.</b> For the whole window the claim is paid on the
/// contract that paid it yesterday, so switching this on changes no payment. The
/// canonical contract is recorded in <see cref="ContractShadowLedger"/>, and
/// <see cref="Services.ShadowPricingRateResolutionService"/> prices the same
/// lines against it and reports the difference. The exit criterion is zero
/// unexplained deltas over one full monthly claim cycle per tenant; phase 5's
/// per-tenant flag is what moves payment across.
/// </para>
///
/// <para>
/// Writes go to the canonical store only, which is the direction the ADR
/// settles on: provider-contracts-service becomes the one contract writer, and
/// the legacy collection becomes read-only and is archived at phase 5. Nothing
/// writes the legacy engine collection in the live path today — the ADR's own
/// finding is that it has no admin API — so there is nothing to regenerate from
/// the canonical contract while the window is open. A seeder that writes the
/// legacy collection does so through its own client and does not pass here.
/// </para>
/// </summary>
public sealed class DualReadProviderContractRepository : IProviderContractRepository
{
    private readonly IProviderContractRepository _legacy;
    private readonly IProviderContractRepository _canonical;
    private readonly ContractShadowLedger _ledger;
    private readonly ContractDualReadOptions _options;
    private readonly ILogger<DualReadProviderContractRepository> _logger;

    // The dual read is scoped to one claim, and the engine looks the same
    // contract up once per line, so this memo spares the canonical store a
    // query per line without adding a cache whose keys could cross the two
    // stores.
    private readonly Dictionary<string, ProviderContract?> _canonicalByKey = new(StringComparer.Ordinal);

    public DualReadProviderContractRepository(
        IProviderContractRepository legacy,
        IProviderContractRepository canonical,
        ContractShadowLedger ledger,
        ContractDualReadOptions options,
        ILogger<DualReadProviderContractRepository> logger)
    {
        _legacy = legacy;
        _canonical = canonical;
        _ledger = ledger;
        _options = options;
        _logger = logger;
    }

    public async Task<ProviderContract?> GetContractAsync(
        string tenantId, string providerNpi, string planId, DateTime serviceDate,
        CancellationToken ct = default)
    {
        var paid = await _legacy.GetContractAsync(tenantId, providerNpi, planId, serviceDate, ct);

        // A tenant outside the window is read exactly as it was before phase 4:
        // one query, no second pass. Recording nothing is what tells the shadow
        // decorator there is nothing to compare.
        if (!_options.IsInWindow(tenantId))
            return paid;

        var key = ContractShadowLedger.KeyFor(tenantId, providerNpi, planId, serviceDate);
        ProviderContract? canonical = null;
        if (_canonicalByKey.TryGetValue(key, out var memoized))
        {
            canonical = memoized;
        }
        else
        {
            // The canonical read is the shadow half: a store that is unreachable,
            // or a document this build cannot deserialize, must not stop a claim
            // from being paid on the contract that already pays it.
            try
            {
                canonical = await _canonical.GetContractAsync(tenantId, providerNpi, planId, serviceDate, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "Dual read: the canonical contract for provider {ProviderNpi} on plan {PlanId} could not be " +
                    "read. The claim is priced on the legacy contract, as it is for the whole window.",
                    providerNpi, planId);
            }

            _canonicalByKey[key] = canonical;
        }

        _ledger.Record(key, paid, canonical);
        return paid;
    }

    /// <summary>
    /// Writes the canonical contract, and not the legacy one. See the type's
    /// summary: the legacy collection is read-only from here on.
    /// </summary>
    public Task<ProviderContract> UpsertAsync(ProviderContract contract, CancellationToken ct = default)
        => _canonical.UpsertAsync(contract, ct);

    /// <summary>
    /// The canonical contracts for the provider, falling back to the legacy ones
    /// where the backfill has not reached them yet. Admin reads, not pricing.
    /// </summary>
    public async Task<IReadOnlyList<ProviderContract>> ListByProviderAsync(
        string tenantId, string providerNpi, CancellationToken ct = default)
    {
        var canonical = await _canonical.ListByProviderAsync(tenantId, providerNpi, ct);
        return canonical.Count > 0
            ? canonical
            : await _legacy.ListByProviderAsync(tenantId, providerNpi, ct);
    }
}
