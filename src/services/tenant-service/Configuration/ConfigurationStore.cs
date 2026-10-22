using CloudHealthOffice.PlanConfiguration;
using MongoDB.Driver;

namespace TenantService.Configuration;

public enum PendingChangeStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>A change to a two-person setting, waiting for a second person (plan-configuration.md §4).</summary>
public sealed class PendingConfigurationChange
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public SettingLevel Level { get; set; }
    public string ScopeId { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool Cleared { get; set; }
    public bool Locked { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Reason { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public DateTime RequestedAt { get; set; }
    public PendingChangeStatus Status { get; set; } = PendingChangeStatus.Pending;
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    /// <summary>The version the approval created.</summary>
    public int? AppliedVersion { get; set; }
}

/// <summary>The audit record of a configuration action: who, when, why, and before and after.</summary>
public sealed class ConfigurationChangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TenantId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public SettingLevel Level { get; set; }
    public string ScopeId { get; set; } = string.Empty;
    /// <summary>Set, Cleared, Requested, Approved, Rejected.</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>The value in force at the node on the change's effective date, before it.</summary>
    public string? Before { get; set; }
    public string? After { get; set; }
    public bool Locked { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string? Reason { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime At { get; set; }
    public int? Version { get; set; }
    public string? PendingId { get; set; }
}

/// <summary>Configuration values, pending changes and the change log. Every query is scoped to one tenant.</summary>
public interface IConfigurationStore
{
    /// <summary>Every version stored for a key at the given nodes.</summary>
    Task<IReadOnlyList<SettingValue>> GetVersionsAsync(string tenantId, string key, IReadOnlyCollection<(SettingLevel Level, string ScopeId)> nodes, CancellationToken ct = default);

    /// <summary>Every version of a key at a level and any more specific level.</summary>
    Task<IReadOnlyList<SettingValue>> GetVersionsAtOrBelowAsync(string tenantId, string key, SettingLevel level, CancellationToken ct = default);

    /// <summary>Adds a version, numbering it after the node's latest; retries a numbering race.</summary>
    Task<SettingValue> AppendAsync(SettingValue value, CancellationToken ct = default);

    Task AddPendingAsync(PendingConfigurationChange change, CancellationToken ct = default);
    Task<PendingConfigurationChange?> GetPendingAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<PendingConfigurationChange>> ListPendingAsync(string tenantId, CancellationToken ct = default);

    /// <summary>Records the decision if the change is still pending; false when someone decided first.</summary>
    Task<bool> TryDecideAsync(string tenantId, string id, PendingChangeStatus status, string decidedBy, DateTime at, string? note, int? appliedVersion, CancellationToken ct = default);

    Task AddChangeAsync(ConfigurationChangeRecord change, CancellationToken ct = default);
    Task<IReadOnlyList<ConfigurationChangeRecord>> ListChangesAsync(string tenantId, string? key, int limit, CancellationToken ct = default);
}

public sealed class MongoConfigurationStore(IMongoDatabase database) : IConfigurationStore
{
    public const string ValuesCollection = "configuration-values";
    public const string PendingCollection = "configuration-pending";
    public const string ChangesCollection = "configuration-changes";

    private readonly IMongoCollection<SettingValue> _values = database.GetCollection<SettingValue>(ValuesCollection);
    private readonly IMongoCollection<PendingConfigurationChange> _pending = database.GetCollection<PendingConfigurationChange>(PendingCollection);
    private readonly IMongoCollection<ConfigurationChangeRecord> _changes = database.GetCollection<ConfigurationChangeRecord>(ChangesCollection);

    public async Task<IReadOnlyList<SettingValue>> GetVersionsAsync(string tenantId, string key,
        IReadOnlyCollection<(SettingLevel Level, string ScopeId)> nodes, CancellationToken ct = default)
    {
        if (nodes.Count == 0) return [];
        var f = Builders<SettingValue>.Filter;
        var any = f.Or(nodes.Select(n => f.Eq(v => v.Level, n.Level) & f.Eq(v => v.ScopeId, n.ScopeId)));
        return await _values.Find(f.Eq(v => v.TenantId, tenantId) & f.Eq(v => v.Key, key) & any).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SettingValue>> GetVersionsAtOrBelowAsync(string tenantId, string key, SettingLevel level, CancellationToken ct = default) =>
        await _values.Find(v => v.TenantId == tenantId && v.Key == key && v.Level >= level)
            .SortBy(v => v.Level).ThenBy(v => v.ScopeId).ThenBy(v => v.Version).ToListAsync(ct);

    public async Task<SettingValue> AppendAsync(SettingValue value, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var latest = await _values.Find(v => v.TenantId == value.TenantId && v.Key == value.Key && v.Level == value.Level && v.ScopeId == value.ScopeId)
                .SortByDescending(v => v.Version).Limit(1).FirstOrDefaultAsync(ct);
            value.Version = (latest?.Version ?? 0) + 1;
            value.Id = SettingValue.IdFor(value.TenantId, value.Key, value.Level, value.ScopeId, value.Version);
            try
            {
                await _values.InsertOneAsync(value, cancellationToken: ct);
                return value;
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey && attempt < 5)
            {
                // Another writer took this number; take the next.
            }
        }
    }

    public Task AddPendingAsync(PendingConfigurationChange change, CancellationToken ct = default) =>
        _pending.InsertOneAsync(change, cancellationToken: ct);

    public async Task<PendingConfigurationChange?> GetPendingAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _pending.Find(p => p.TenantId == tenantId && p.Id == id).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<PendingConfigurationChange>> ListPendingAsync(string tenantId, CancellationToken ct = default) =>
        await _pending.Find(p => p.TenantId == tenantId && p.Status == PendingChangeStatus.Pending)
            .SortBy(p => p.RequestedAt).ToListAsync(ct);

    public async Task<bool> TryDecideAsync(string tenantId, string id, PendingChangeStatus status, string decidedBy, DateTime at,
        string? note, int? appliedVersion, CancellationToken ct = default)
    {
        var result = await _pending.UpdateOneAsync(
            p => p.TenantId == tenantId && p.Id == id && p.Status == PendingChangeStatus.Pending,
            Builders<PendingConfigurationChange>.Update
                .Set(p => p.Status, status).Set(p => p.DecidedBy, decidedBy).Set(p => p.DecidedAt, at)
                .Set(p => p.DecisionNote, note).Set(p => p.AppliedVersion, appliedVersion),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public Task AddChangeAsync(ConfigurationChangeRecord change, CancellationToken ct = default) =>
        _changes.InsertOneAsync(change, cancellationToken: ct);

    public async Task<IReadOnlyList<ConfigurationChangeRecord>> ListChangesAsync(string tenantId, string? key, int limit, CancellationToken ct = default)
    {
        var f = Builders<ConfigurationChangeRecord>.Filter;
        var filter = f.Eq(c => c.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(key)) filter &= f.Eq(c => c.Key, key);
        return await _changes.Find(filter).SortByDescending(c => c.At).Limit(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
    }

    /// <summary>Creates the indexes (idempotent).</summary>
    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await _values.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<SettingValue>(Builders<SettingValue>.IndexKeys
                .Ascending(v => v.TenantId).Ascending(v => v.Key).Ascending(v => v.Level).Ascending(v => v.ScopeId).Descending(v => v.Version),
                new CreateIndexOptions { Name = "ix_tenant_key_node_version", Unique = true }),
        ], ct);
        await _pending.Indexes.CreateOneAsync(new CreateIndexModel<PendingConfigurationChange>(
            Builders<PendingConfigurationChange>.IndexKeys.Ascending(p => p.TenantId).Ascending(p => p.Status).Ascending(p => p.RequestedAt),
            new CreateIndexOptions { Name = "ix_tenant_status_requested" }), cancellationToken: ct);
        await _changes.Indexes.CreateOneAsync(new CreateIndexModel<ConfigurationChangeRecord>(
            Builders<ConfigurationChangeRecord>.IndexKeys.Ascending(c => c.TenantId).Ascending(c => c.Key).Descending(c => c.At),
            new CreateIndexOptions { Name = "ix_tenant_key_at" }), cancellationToken: ct);
    }
}
