using MongoDB.Driver;

namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>The recovery worklist's findings and the plan's lookback settings, in their own collections.</summary>
public interface IShadowFindingRepository
{
    Task<IReadOnlyDictionary<string, ShadowFinding>> GetManyAsync(string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default);

    Task SaveManyAsync(IReadOnlyList<ShadowFinding> findings, CancellationToken ct = default);

    Task<ShadowFinding?> GetAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Replaces the finding only if its status is still <paramref name="expectedStatus"/>; false otherwise.</summary>
    Task<bool> ReplaceIfStatusAsync(ShadowFinding finding, string expectedStatus, CancellationToken ct = default);

    /// <summary>The tenant's findings, largest amount first, optionally one status, direction or provider.</summary>
    Task<IReadOnlyList<ShadowFinding>> ListAsync(
        string tenantId, string? status, string? direction, string? billingProviderNpi, int limit, CancellationToken ct = default);

    Task<ShadowRecoveryLookbackSettings?> GetLookbackAsync(string tenantId, CancellationToken ct = default);

    Task SaveLookbackAsync(ShadowRecoveryLookbackSettings settings, CancellationToken ct = default);
}

public sealed class ShadowFindingRepositoryMongo : IShadowFindingRepository
{
    public const string FindingsCollectionName = "ShadowFindings";
    public const string LookbackCollectionName = "ShadowRecoveryLookbacks";

    private readonly IMongoCollection<ShadowFinding> _findings;
    private readonly IMongoCollection<ShadowRecoveryLookbackSettings> _lookbacks;

    public ShadowFindingRepositoryMongo(IMongoDatabase database)
    {
        _findings = database.GetCollection<ShadowFinding>(FindingsCollectionName);
        _lookbacks = database.GetCollection<ShadowRecoveryLookbackSettings>(LookbackCollectionName);
    }

    public static void EnsureIndexes(IMongoDatabase database, CancellationToken ct = default)
    {
        var findings = database.GetCollection<ShadowFinding>(FindingsCollectionName);
        findings.Indexes.CreateOne(new CreateIndexModel<ShadowFinding>(
            Builders<ShadowFinding>.IndexKeys.Ascending(f => f.TenantId).Ascending(f => f.Status).Descending(f => f.Amount),
            new CreateIndexOptions { Name = "tenant_status_amount" }), cancellationToken: ct);
        findings.Indexes.CreateOne(new CreateIndexModel<ShadowFinding>(
            Builders<ShadowFinding>.IndexKeys.Ascending(f => f.TenantId).Ascending(f => f.BillingProviderNpi),
            new CreateIndexOptions { Name = "tenant_provider" }), cancellationToken: ct);
    }

    public async Task<IReadOnlyDictionary<string, ShadowFinding>> GetManyAsync(
        string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return new Dictionary<string, ShadowFinding>(StringComparer.Ordinal);
        var list = await _findings.Find(f => f.TenantId == tenantId && ids.Contains(f.Id)).ToListAsync(ct);
        return list.ToDictionary(f => f.Id, StringComparer.Ordinal);
    }

    public async Task SaveManyAsync(IReadOnlyList<ShadowFinding> findings, CancellationToken ct = default)
    {
        if (findings.Count == 0) return;
        var writes = findings.Select(f => new ReplaceOneModel<ShadowFinding>(
            Builders<ShadowFinding>.Filter.Eq(x => x.Id, f.Id) & Builders<ShadowFinding>.Filter.Eq(x => x.TenantId, f.TenantId), f)
        { IsUpsert = true });
        foreach (var chunk in writes.Chunk(500))
            await _findings.BulkWriteAsync(chunk, new BulkWriteOptions { IsOrdered = false }, ct);
    }

    public async Task<ShadowFinding?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        await _findings.Find(f => f.TenantId == tenantId && f.Id == id).FirstOrDefaultAsync(ct);

    public async Task<bool> ReplaceIfStatusAsync(ShadowFinding finding, string expectedStatus, CancellationToken ct = default)
    {
        var result = await _findings.ReplaceOneAsync(
            f => f.TenantId == finding.TenantId && f.Id == finding.Id && f.Status == expectedStatus, finding, cancellationToken: ct);
        return result.ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<ShadowFinding>> ListAsync(
        string tenantId, string? status, string? direction, string? billingProviderNpi, int limit, CancellationToken ct = default)
    {
        var b = Builders<ShadowFinding>.Filter;
        var filter = b.Eq(f => f.TenantId, tenantId);
        if (!string.IsNullOrWhiteSpace(status)) filter &= b.Eq(f => f.Status, status);
        if (!string.IsNullOrWhiteSpace(direction)) filter &= b.Eq(f => f.Direction, direction);
        if (!string.IsNullOrWhiteSpace(billingProviderNpi)) filter &= b.Eq(f => f.BillingProviderNpi, billingProviderNpi);
        return await _findings.Find(filter).SortByDescending(f => f.Amount).Limit(limit).ToListAsync(ct);
    }

    public async Task<ShadowRecoveryLookbackSettings?> GetLookbackAsync(string tenantId, CancellationToken ct = default) =>
        await _lookbacks.Find(l => l.Id == tenantId).FirstOrDefaultAsync(ct);

    public Task SaveLookbackAsync(ShadowRecoveryLookbackSettings settings, CancellationToken ct = default) =>
        _lookbacks.ReplaceOneAsync(l => l.Id == settings.Id, settings, new ReplaceOptions { IsUpsert = true }, ct);
}

/// <summary>Adds a finished run's payment differences to the recovery worklist.</summary>
public interface IShadowFindingService
{
    /// <summary>Folds the run's results into the findings; returns how many were added or updated.</summary>
    Task<int> AddRunAsync(ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default);
}

public sealed class ShadowFindingService : IShadowFindingService
{
    private readonly IShadowFindingRepository _store;
    private readonly TimeProvider _clock;

    public ShadowFindingService(IShadowFindingRepository store, TimeProvider? clock = null)
    {
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<int> AddRunAsync(
        ShadowAdjudicationRun run, IReadOnlyList<ShadowAdjudicationResult> results, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(results);
        var compared = results
            .Where(r => r.Kind is not (ShadowDiscrepancyKind.NotComparable or ShadowDiscrepancyKind.ChoUndecided))
            .ToList();
        if (compared.Count == 0) return 0;

        var saved = 0;
        foreach (var page in compared.Chunk(1000))
        {
            var ids = page.Select(r => ShadowFinding.IdFor(run.TenantId, r.ClaimId)).Distinct(StringComparer.Ordinal).ToList();
            var existing = await _store.GetManyAsync(run.TenantId, ids, ct).ConfigureAwait(false);
            var changed = ShadowFindingBuilder.Merge(run, page, existing, _clock.GetUtcNow().UtcDateTime);
            await _store.SaveManyAsync(changed, ct).ConfigureAwait(false);
            saved += changed.Count;
        }
        return saved;
    }
}

/// <summary>In-memory findings, for tests and hosts without MongoDB. Hands out copies, as a database would.</summary>
public sealed class InMemoryShadowFindingRepository : IShadowFindingRepository
{
    private readonly Dictionary<string, ShadowFinding> _findings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShadowRecoveryLookbackSettings> _lookbacks = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private static T Copy<T>(T value) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value))!;

    public Task<IReadOnlyDictionary<string, ShadowFinding>> GetManyAsync(
        string tenantId, IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyDictionary<string, ShadowFinding> found = ids
                .Distinct(StringComparer.Ordinal)
                .Where(id => _findings.TryGetValue(id, out var f) && f.TenantId == tenantId)
                .ToDictionary(id => id, id => Copy(_findings[id]), StringComparer.Ordinal);
            return Task.FromResult(found);
        }
    }

    public Task SaveManyAsync(IReadOnlyList<ShadowFinding> findings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var f in findings) _findings[f.Id] = Copy(f);
        }
        return Task.CompletedTask;
    }

    public Task<ShadowFinding?> GetAsync(string tenantId, string id, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_findings.TryGetValue(id, out var f) && f.TenantId == tenantId ? Copy(f) : null);
    }

    public Task<bool> ReplaceIfStatusAsync(ShadowFinding finding, string expectedStatus, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_findings.TryGetValue(finding.Id, out var current) || current.TenantId != finding.TenantId
                || current.Status != expectedStatus)
                return Task.FromResult(false);
            _findings[finding.Id] = Copy(finding);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ShadowFinding>> ListAsync(
        string tenantId, string? status, string? direction, string? billingProviderNpi, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ShadowFinding> list = _findings.Values
                .Where(f => f.TenantId == tenantId
                            && (string.IsNullOrWhiteSpace(status) || f.Status == status)
                            && (string.IsNullOrWhiteSpace(direction) || f.Direction == direction)
                            && (string.IsNullOrWhiteSpace(billingProviderNpi) || f.BillingProviderNpi == billingProviderNpi))
                .OrderByDescending(f => f.Amount)
                .Take(limit)
                .Select(Copy)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<ShadowRecoveryLookbackSettings?> GetLookbackAsync(string tenantId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_lookbacks.TryGetValue(tenantId, out var l) ? Copy(l) : null);
    }

    public Task SaveLookbackAsync(ShadowRecoveryLookbackSettings settings, CancellationToken ct = default)
    {
        lock (_gate) _lookbacks[settings.Id] = Copy(settings);
        return Task.CompletedTask;
    }
}
