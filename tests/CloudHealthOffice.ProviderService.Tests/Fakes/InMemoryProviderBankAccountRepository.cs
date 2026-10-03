using System.Text.Json;
using ProviderService.Models;
using ProviderService.Repositories;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IProviderBankAccountRepository"/> with the same
/// revision-conditional save as the Mongo and Cosmos implementations. Records
/// are deep-cloned on the way in and out, as a database round-trip would.
/// </summary>
public sealed class InMemoryProviderBankAccountRepository : IProviderBankAccountRepository
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, ProviderBankAccountRecord> _records = new();
    private readonly object _gate = new();

    /// <summary>Runs after the next read, before its caller saves: simulates a concurrent writer.</summary>
    public Action<InMemoryProviderBankAccountRepository>? AfterNextGet { get; set; }

    public IReadOnlyCollection<ProviderBankAccountRecord> Records
    {
        get { lock (_gate) return _records.Values.Select(Clone).ToList(); }
    }

    public Task<ProviderBankAccountRecord?> GetAsync(string tenantId, string providerId, CancellationToken ct = default)
    {
        ProviderBankAccountRecord? result;
        lock (_gate)
        {
            result = _records.TryGetValue(ProviderBankAccountRecord.KeyFor(tenantId, providerId), out var r) ? Clone(r) : null;
        }
        var hook = AfterNextGet;
        AfterNextGet = null;
        hook?.Invoke(this);
        return Task.FromResult(result);
    }

    public Task<bool> SaveAsync(ProviderBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var key = ProviderBankAccountRecord.KeyFor(record.TenantId, record.ProviderId);
            _records.TryGetValue(key, out var stored);
            if ((stored?.Revision ?? 0) != expectedRevision) return Task.FromResult(false);

            record.Id = key;
            record.Revision = expectedRevision + 1;
            record.UpdatedAt = DateTime.UtcNow;
            _records[key] = Clone(record);
            return Task.FromResult(true);
        }
    }

    /// <summary>Bumps every stored record's revision, as another writer's save would.</summary>
    public void TouchAll()
    {
        lock (_gate)
        {
            foreach (var r in _records.Values) r.Revision++;
        }
    }

    private static ProviderBankAccountRecord Clone(ProviderBankAccountRecord r)
        => JsonSerializer.Deserialize<ProviderBankAccountRecord>(JsonSerializer.Serialize(r, Json), Json)!;
}
