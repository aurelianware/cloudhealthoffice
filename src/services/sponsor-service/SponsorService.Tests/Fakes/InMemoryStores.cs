using System.Collections.Concurrent;
using System.Text.Json;
using SponsorService.Models;
using SponsorService.Repositories;

namespace SponsorService.Tests.Fakes;

/// <summary>
/// Raw bank-account storage: keeps a serialized copy of exactly what the
/// service saved (encrypted numbers included), with the same conditional
/// revision rule as the Mongo and Cosmos repositories.
/// </summary>
public sealed class InMemorySponsorBankAccountRepository : ISponsorBankAccountRepository
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> _docs = new();

    /// <summary>Runs before each save is applied (simulates a concurrent writer).</summary>
    public Action? BeforeSave { get; set; }

    public void Clear()
    {
        _docs.Clear();
        BeforeSave = null;
    }

    /// <summary>The stored document as JSON, exactly as written.</summary>
    public string? RawJson(string tenantId, string groupNumber)
        => _docs.TryGetValue(SponsorBankAccountRecord.KeyFor(tenantId, groupNumber), out var json) ? json : null;

    /// <summary>The stored document, not decrypted.</summary>
    public SponsorBankAccountRecord? Raw(string tenantId, string groupNumber)
        => RawJson(tenantId, groupNumber) is { } json ? JsonSerializer.Deserialize<SponsorBankAccountRecord>(json, Options) : null;

    /// <summary>Writes a document directly (legacy data, or another writer).</summary>
    public void Put(SponsorBankAccountRecord record)
    {
        record.Id = SponsorBankAccountRecord.KeyFor(record.TenantId, record.GroupNumber);
        _docs[record.Id] = JsonSerializer.Serialize(record, Options);
    }

    public Task<SponsorBankAccountRecord?> GetAsync(string tenantId, string groupNumber, CancellationToken ct = default)
        => Task.FromResult(Raw(tenantId, groupNumber));

    public Task<bool> SaveAsync(SponsorBankAccountRecord record, long expectedRevision, CancellationToken ct = default)
    {
        BeforeSave?.Invoke();
        lock (_docs)
        {
            var key = SponsorBankAccountRecord.KeyFor(record.TenantId, record.GroupNumber);
            var current = Raw(record.TenantId, record.GroupNumber);
            var currentRevision = current?.Revision ?? 0;
            if (currentRevision != expectedRevision || (expectedRevision == 0 && current != null))
                return Task.FromResult(false);

            record.Id = key;
            record.Revision = expectedRevision + 1;
            record.UpdatedAt = DateTime.UtcNow;
            _docs[key] = JsonSerializer.Serialize(record, Options);
            return Task.FromResult(true);
        }
    }
}

/// <summary>Raw sponsor storage (what Mongo or Cosmos would hold), copies in and out.</summary>
public sealed class InMemorySponsorRepository : ISponsorRepository
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> _docs = new();

    public void Clear() => _docs.Clear();

    public void Put(Sponsor sponsor) => _docs[Key(sponsor.TenantId, sponsor.Id)] = JsonSerializer.Serialize(sponsor, Options);

    /// <summary>The stored sponsor, not decrypted.</summary>
    public Sponsor? RawByGroup(string tenantId, string groupNumber)
        => All(tenantId).FirstOrDefault(s => s.GroupNumber == groupNumber);

    private IEnumerable<Sponsor> All(string tenantId)
        => _docs.Values.Select(v => JsonSerializer.Deserialize<Sponsor>(v, Options)!).Where(s => s.TenantId == tenantId);

    private static string Key(string tenantId, string id) => $"{tenantId}|{id}";

    public Task<Sponsor?> GetByIdAsync(string tenantId, string id)
        => Task.FromResult(_docs.TryGetValue(Key(tenantId, id), out var json) ? JsonSerializer.Deserialize<Sponsor>(json, Options) : null);

    public Task<Sponsor?> GetByGroupNumberAsync(string tenantId, string groupNumber)
        => Task.FromResult(RawByGroup(tenantId, groupNumber));

    public Task<(IEnumerable<Sponsor> Items, string? ContinuationToken, int TotalCount)> GetPagedAsync(
        string tenantId, SponsorStatus? status = null, bool activeOnly = false, LineOfBusiness? lineOfBusiness = null,
        int pageSize = 20, string? continuationToken = null)
    {
        var items = All(tenantId).ToList();
        return Task.FromResult(((IEnumerable<Sponsor>)items, (string?)null, items.Count));
    }

    public Task<Sponsor> CreateAsync(Sponsor sponsor)
    {
        Put(sponsor);
        return Task.FromResult(sponsor);
    }

    public Task<Sponsor> UpdateAsync(Sponsor sponsor)
    {
        Put(sponsor);
        return Task.FromResult(sponsor);
    }

    public Task<bool> UpdateStatusAsync(string tenantId, string id, SponsorStatusChange change) => Task.FromResult(false);

    public Task DeleteAsync(string tenantId, string id)
    {
        _docs.TryRemove(Key(tenantId, id), out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string tenantId, string groupNumber) => Task.FromResult(RawByGroup(tenantId, groupNumber) != null);

    public Task<int> GetCountAsync(string tenantId, SponsorStatus? status = null) => Task.FromResult(All(tenantId).Count());
}
