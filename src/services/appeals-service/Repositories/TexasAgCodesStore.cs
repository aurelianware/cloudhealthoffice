using System.Collections.Concurrent;
using AppealsService.Reports.Texas;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace AppealsService.Repositories;

/// <summary>Each case's Texas HHSC reporting codes (<see cref="TexasAgCodes"/>), one document per tenant and appeal.</summary>
public interface ITexasAgCodesStore
{
    Task<TexasAgCodes?> GetAsync(string tenantId, string appealId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, TexasAgCodes>> GetManyAsync(string tenantId, IReadOnlyCollection<string> appealIds, CancellationToken ct = default);

    /// <summary>Replaces the case's codes, keeping the previous version in its history.</summary>
    Task<TexasAgCodes> SaveAsync(TexasAgCodes codes, CancellationToken ct = default);
}

public sealed class TexasAgCodesStoreMongo : ITexasAgCodesStore
{
    public const string CollectionName = "AppealTexasCodes";

    private readonly IMongoCollection<TexasAgCodesDocument> _rows;

    public TexasAgCodesStoreMongo(IMongoDatabase db) => _rows = db.GetCollection<TexasAgCodesDocument>(CollectionName);

    public static string KeyFor(string tenantId, string appealId) => $"{tenantId}:{appealId}";

    public async Task<TexasAgCodes?> GetAsync(string tenantId, string appealId, CancellationToken ct = default) =>
        (await _rows.Find(r => r.Codes.TenantId == tenantId && r.Id == KeyFor(tenantId, appealId)).FirstOrDefaultAsync(ct))?.Codes;

    public async Task<IReadOnlyDictionary<string, TexasAgCodes>> GetManyAsync(
        string tenantId, IReadOnlyCollection<string> appealIds, CancellationToken ct = default)
    {
        if (appealIds.Count == 0) return new Dictionary<string, TexasAgCodes>();
        var keys = appealIds.Select(id => KeyFor(tenantId, id)).ToList();
        var rows = await _rows.Find(r => r.Codes.TenantId == tenantId && keys.Contains(r.Id)).ToListAsync(ct);
        return rows.ToDictionary(r => r.Codes.AppealId, r => r.Codes, StringComparer.Ordinal);
    }

    public async Task<TexasAgCodes> SaveAsync(TexasAgCodes codes, CancellationToken ct = default)
    {
        var key = KeyFor(codes.TenantId, codes.AppealId);
        var previous = (await _rows.Find(r => r.Codes.TenantId == codes.TenantId && r.Id == key).FirstOrDefaultAsync(ct))?.Codes;
        codes.History = TexasAgCodesHistory.Next(previous);
        await _rows.ReplaceOneAsync(r => r.Codes.TenantId == codes.TenantId && r.Id == key,
            new TexasAgCodesDocument { Id = key, Codes = codes }, new ReplaceOptions { IsUpsert = true }, ct);
        return codes;
    }
}

[BsonIgnoreExtraElements]
public sealed class TexasAgCodesDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    public TexasAgCodes Codes { get; set; } = new();
}

public static class TexasAgCodesHistory
{
    public static List<TexasAgCodesRevision> Next(TexasAgCodes? previous)
    {
        if (previous is null) return new();
        var history = previous.History.ToList();
        var snapshot = previous.CopyCodes();
        snapshot.History = new();
        history.Add(new TexasAgCodesRevision
        {
            UpdatedAt = previous.UpdatedAt,
            UpdatedBy = previous.UpdatedBy,
            CodesJson = System.Text.Json.JsonSerializer.Serialize(snapshot),
        });
        return history.Skip(Math.Max(0, history.Count - TexasAgCodes.MaxHistory)).ToList();
    }
}

/// <summary>For tests and local development.</summary>
public sealed class InMemoryTexasAgCodesStore : ITexasAgCodesStore
{
    private readonly ConcurrentDictionary<string, TexasAgCodes> _rows = new(StringComparer.Ordinal);

    public Task<TexasAgCodes?> GetAsync(string tenantId, string appealId, CancellationToken ct = default) =>
        Task.FromResult(_rows.TryGetValue(TexasAgCodesStoreMongo.KeyFor(tenantId, appealId), out var c) ? c : null);

    public Task<IReadOnlyDictionary<string, TexasAgCodes>> GetManyAsync(
        string tenantId, IReadOnlyCollection<string> appealIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, TexasAgCodes>>(appealIds
            .Select(id => _rows.TryGetValue(TexasAgCodesStoreMongo.KeyFor(tenantId, id), out var c) ? c : null)
            .OfType<TexasAgCodes>()
            .ToDictionary(c => c.AppealId, StringComparer.Ordinal));

    public Task<TexasAgCodes> SaveAsync(TexasAgCodes codes, CancellationToken ct = default)
    {
        var key = TexasAgCodesStoreMongo.KeyFor(codes.TenantId, codes.AppealId);
        _rows.TryGetValue(key, out var previous);
        codes.History = TexasAgCodesHistory.Next(previous);
        _rows[key] = codes;
        return Task.FromResult(codes);
    }
}
