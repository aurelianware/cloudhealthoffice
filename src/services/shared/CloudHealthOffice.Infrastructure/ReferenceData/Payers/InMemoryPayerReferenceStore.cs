using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.ReferenceData.Payers;

/// <summary>
/// Process-local payer store used by CI, Development, and as the runtime cache
/// when Mongo is not configured. Tenant overlays are keyed by tenant id and
/// never returned for a different tenant.
/// </summary>
internal sealed class InMemoryPayerReferenceStore : IPayerReferenceStore
{
    private readonly ConcurrentDictionary<string, PayerReference> _payers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, PayerTenantOverride> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, PayerDirectorySyncStatus> _sync =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<PayerReference?> GetByIdAsync(string id, CancellationToken ct)
    {
        _payers.TryGetValue(id, out var payer);
        return Task.FromResult(payer);
    }

    public Task<IReadOnlyList<PayerReference>> GetByIdsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = new List<PayerReference>();
        foreach (var id in ids)
        {
            if (_payers.TryGetValue(id, out var payer))
            {
                list.Add(Clone(payer));
            }
        }
        return Task.FromResult<IReadOnlyList<PayerReference>>(list);
    }

    public Task<IReadOnlyList<PayerReference>> FindExactAsync(string normalizedToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(normalizedToken))
        {
            return Task.FromResult<IReadOnlyList<PayerReference>>(Array.Empty<PayerReference>());
        }

        var matches = _payers.Values
            .Where(p => PayerLookup.Tokens(p).Contains(normalizedToken))
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<PayerReference>>(matches);
    }

    public Task<IReadOnlyList<PayerReference>> SearchAsync(PayerSearchQuery query, CancellationToken ct)
    {
        IEnumerable<PayerReference> items = _payers.Values;

        if (query.Active is { } active)
        {
            items = items.Where(p => p.Active == active);
        }

        if (!string.IsNullOrWhiteSpace(query.Id))
        {
            items = items.Where(p => PayerLookup.EqualsNormalized(p.Id, query.Id));
        }

        if (!string.IsNullOrWhiteSpace(query.ExternalValue) ||
            !string.IsNullOrWhiteSpace(query.ExternalSystem) ||
            !string.IsNullOrWhiteSpace(query.ExternalType))
        {
            items = items.Where(p => p.ExternalIdentifiers.Any(id =>
                (string.IsNullOrWhiteSpace(query.ExternalSystem) ||
                 PayerLookup.EqualsNormalized(id.System, query.ExternalSystem)) &&
                (string.IsNullOrWhiteSpace(query.ExternalType) ||
                 PayerLookup.EqualsNormalized(id.Type, query.ExternalType)) &&
                (string.IsNullOrWhiteSpace(query.ExternalValue) ||
                 PayerLookup.EqualsNormalized(id.Value, query.ExternalValue))));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var token = PayerLookup.Normalize(query.Text);
            items = items.Where(p =>
                PayerLookup.Tokens(p).Contains(token) ||
                (!string.IsNullOrWhiteSpace(p.Name) &&
                 PayerLookup.Normalize(p.Name).Contains(token, StringComparison.Ordinal)));
        }

        var take = query.MaxResults <= 0 ? 50 : Math.Min(query.MaxResults, 500);
        var results = items
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<PayerReference>>(results);
    }

    public Task UpsertAsync(PayerReference payer, CancellationToken ct)
    {
        _payers[payer.Id] = Clone(payer);
        return Task.CompletedTask;
    }

    public Task UpsertManyAsync(IReadOnlyList<PayerReference> payers, CancellationToken ct)
    {
        foreach (var payer in payers)
        {
            _payers[payer.Id] = Clone(payer);
        }
        return Task.CompletedTask;
    }

    public Task<int> DisableMissingFromSourceAsync(
        string source, IReadOnlyCollection<string> presentIds, DateTimeOffset at, CancellationToken ct)
    {
        var present = new HashSet<string>(presentIds, StringComparer.OrdinalIgnoreCase);
        var disabled = 0;
        foreach (var payer in _payers.Values)
        {
            if (!string.Equals(payer.Provenance.Source, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (present.Contains(payer.Id) || !payer.Active)
            {
                continue;
            }

            payer.Active = false;
            payer.Provenance.LastSyncedAt = at;
            disabled++;
        }

        return Task.FromResult(disabled);
    }

    public Task<PayerTenantOverride?> GetTenantOverrideAsync(string tenantId, string payerId, CancellationToken ct)
    {
        _overrides.TryGetValue(OverrideKey(tenantId, payerId), out var overlay);
        return Task.FromResult(overlay is null ? null : Clone(overlay));
    }

    public Task UpsertTenantOverrideAsync(PayerTenantOverride overlay, CancellationToken ct)
    {
        _overrides[OverrideKey(overlay.TenantId, overlay.PayerId)] = Clone(overlay);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PayerTenantOverride>> ListTenantOverridesAsync(string tenantId, CancellationToken ct)
    {
        var prefix = PayerLookup.Normalize(tenantId) + "|";
        var list = _overrides
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => Clone(kv.Value))
            .ToList();
        return Task.FromResult<IReadOnlyList<PayerTenantOverride>>(list);
    }

    public Task<PayerDirectorySyncStatus?> GetSyncStatusAsync(string source, CancellationToken ct)
    {
        _sync.TryGetValue(source, out var status);
        return Task.FromResult(status);
    }

    public Task SaveSyncStatusAsync(PayerDirectorySyncStatus status, CancellationToken ct)
    {
        _sync[status.Source] = status;
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(_payers.Count);

    private static string OverrideKey(string tenantId, string payerId) =>
        $"{PayerLookup.Normalize(tenantId)}|{PayerLookup.Normalize(payerId)}";

    private static PayerReference Clone(PayerReference p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Aliases = p.Aliases.ToList(),
        ExternalIdentifiers = p.ExternalIdentifiers
            .Select(i => new PayerExternalIdentifier { System = i.System, Type = i.Type, Value = i.Value })
            .ToList(),
        SupportedTransactions = p.SupportedTransactions
            .Select(t => new PayerTransactionCapability { Transaction = t.Transaction, Support = t.Support })
            .ToList(),
        EnrollmentRequirements = p.EnrollmentRequirements
            .Select(e => new PayerEnrollmentRequirement
            {
                Transaction = e.Transaction,
                Required = e.Required,
                ProcessType = e.ProcessType,
                Timeframe = e.Timeframe
            })
            .ToList(),
        Active = p.Active,
        Provenance = new PayerReferenceProvenance
        {
            Source = p.Provenance.Source,
            SourceUpdatedAt = p.Provenance.SourceUpdatedAt,
            LastSyncedAt = p.Provenance.LastSyncedAt
        },
        Metadata = new Dictionary<string, string>(p.Metadata, StringComparer.OrdinalIgnoreCase)
    };

    private static PayerTenantOverride Clone(PayerTenantOverride o) => new()
    {
        TenantId = o.TenantId,
        PayerId = o.PayerId,
        Enabled = o.Enabled,
        PreferredAlias = o.PreferredAlias,
        ExternalIdentifiers = o.ExternalIdentifiers
            .Select(i => new PayerExternalIdentifier { System = i.System, Type = i.Type, Value = i.Value })
            .ToList(),
        EnrolledTransactions = o.EnrolledTransactions.ToList()
    };
}
