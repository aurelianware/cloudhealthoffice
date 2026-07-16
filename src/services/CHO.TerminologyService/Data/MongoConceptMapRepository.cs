using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services;
using MongoDB.Driver;

namespace CHO.TerminologyService.Data;

/// <summary>
/// MongoDB-backed repository for ConceptMap entries and map versions.
/// Uses the existing CHO MongoDB pod (mongodb-0 in the cloudhealthoffice namespace).
/// 
/// Collections:
///   - concept_map_entries: The crosswalk data (indexed on source/target code + system)
///   - map_versions: Version tracking for audit and rollback
/// 
/// Indexes ensure sub-millisecond lookup for $translate operations.
/// </summary>
public class MongoConceptMapRepository : IConceptMapRepository
{
    private readonly IMongoCollection<ConceptMapEntry> _entries;
    private readonly IMongoCollection<MapVersion> _versions;
    private readonly ILogger<MongoConceptMapRepository> _logger;

    public MongoConceptMapRepository(IMongoDatabase database, ILogger<MongoConceptMapRepository> logger)
    {
        _entries = database.GetCollection<ConceptMapEntry>("concept_map_entries");
        _versions = database.GetCollection<MapVersion>("map_versions");
        _logger = logger;

        EnsureIndexes();
    }

    private void EnsureIndexes()
    {
        // Primary lookup index: source system + code + target system
        _entries.Indexes.CreateOne(new CreateIndexModel<ConceptMapEntry>(
            Builders<ConceptMapEntry>.IndexKeys
                .Ascending(e => e.SourceSystem)
                .Ascending(e => e.SourceCode)
                .Ascending(e => e.TargetSystem)
                .Ascending(e => e.TenantId),
            new CreateIndexOptions { Name = "idx_source_lookup" }));

        // Reverse lookup index: target → source
        _entries.Indexes.CreateOne(new CreateIndexModel<ConceptMapEntry>(
            Builders<ConceptMapEntry>.IndexKeys
                .Ascending(e => e.TargetSystem)
                .Ascending(e => e.TargetCode)
                .Ascending(e => e.SourceSystem),
            new CreateIndexOptions { Name = "idx_target_lookup" }));

        // Map version index
        _entries.Indexes.CreateOne(new CreateIndexModel<ConceptMapEntry>(
            Builders<ConceptMapEntry>.IndexKeys.Ascending(e => e.MapVersionId),
            new CreateIndexOptions { Name = "idx_map_version" }));

        // Versions collection: active version lookup
        _versions.Indexes.CreateOne(new CreateIndexModel<MapVersion>(
            Builders<MapVersion>.IndexKeys
                .Ascending(v => v.SourceSystem)
                .Ascending(v => v.TargetSystem)
                .Descending(v => v.IsActive),
            new CreateIndexOptions { Name = "idx_active_version" }));

        _logger.LogInformation("MongoDB indexes ensured for concept_map_entries and map_versions");
    }

    public async Task<List<ConceptMapEntry>> FindBySourceCodeAsync(
        string sourceSystem, string sourceCode, string targetSystem,
        string? tenantId = null, CancellationToken ct = default)
    {
        // Get active map versions for this system pair
        var activeVersions = await _versions.Find(v =>
            v.SourceSystem == sourceSystem &&
            v.TargetSystem == targetSystem &&
            v.IsActive)
            .ToListAsync(ct);

        var activeVersionIds = activeVersions.Select(v => v.Id).ToHashSet();

        var filter = Builders<ConceptMapEntry>.Filter.And(
            Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceSystem, sourceSystem),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceCode, sourceCode),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetSystem, targetSystem),
            Builders<ConceptMapEntry>.Filter.Or(
                // Global entries from active map versions
                Builders<ConceptMapEntry>.Filter.And(
                    Builders<ConceptMapEntry>.Filter.In(e => e.MapVersionId, activeVersionIds),
                    Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, false)
                ),
                // Plan-specific overrides (if tenantId provided)
                tenantId != null
                    ? Builders<ConceptMapEntry>.Filter.And(
                        Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, true),
                        Builders<ConceptMapEntry>.Filter.Eq(e => e.TenantId, tenantId))
                    : Builders<ConceptMapEntry>.Filter.Eq(e => e.Id, "__never_match__")
            )
        );

        return await _entries.Find(filter)
            .Sort(Builders<ConceptMapEntry>.Sort
                .Descending(e => e.IsOverride) // Overrides first
                .Ascending(e => e.Priority))
            .ToListAsync(ct);
    }

    public async Task<List<ConceptMapEntry>> FindByTargetCodeAsync(
        string targetSystem, string targetCode, string sourceSystem,
        string? tenantId = null, CancellationToken ct = default)
    {
        var filter = Builders<ConceptMapEntry>.Filter.And(
            Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetSystem, targetSystem),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetCode, targetCode),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceSystem, sourceSystem)
        );

        return await _entries.Find(filter)
            .Sort(Builders<ConceptMapEntry>.Sort.Ascending(e => e.Priority))
            .ToListAsync(ct);
    }

    public async Task<List<ConceptMapEntry>> FindDisplaysByCodeAsync(
        string system, string code, string? tenantId = null, CancellationToken ct = default)
    {
        var activeVersionIds = await _versions.Find(v => v.IsActive)
            .Project(v => v.Id)
            .ToListAsync(ct);

        var codeFilter = Builders<ConceptMapEntry>.Filter.Or(
            Builders<ConceptMapEntry>.Filter.And(
                Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceSystem, system),
                Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceCode, code)),
            Builders<ConceptMapEntry>.Filter.And(
                Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetSystem, system),
                Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetCode, code)));

        var activeFilter = Builders<ConceptMapEntry>.Filter.Or(
            Builders<ConceptMapEntry>.Filter.And(
                Builders<ConceptMapEntry>.Filter.In(e => e.MapVersionId, activeVersionIds),
                Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, false)),
            tenantId != null
                ? Builders<ConceptMapEntry>.Filter.And(
                    Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, true),
                    Builders<ConceptMapEntry>.Filter.Eq(e => e.TenantId, tenantId))
                : Builders<ConceptMapEntry>.Filter.Eq(e => e.Id, "__never_match__"));

        var filter = Builders<ConceptMapEntry>.Filter.And(codeFilter, activeFilter);

        return await _entries.Find(filter)
            .Sort(Builders<ConceptMapEntry>.Sort
                .Descending(e => e.IsOverride)
                .Ascending(e => e.Priority))
            .Limit(20)
            .ToListAsync(ct);
    }

    public async Task BulkInsertAsync(List<ConceptMapEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;

        // Insert in batches of 5000 for MongoDB performance
        const int batchSize = 5000;
        for (int i = 0; i < entries.Count; i += batchSize)
        {
            var batch = entries.Skip(i).Take(batchSize).ToList();
            await _entries.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false }, ct);
            _logger.LogDebug("Inserted batch {Start}-{End} of {Total}",
                i, Math.Min(i + batchSize, entries.Count), entries.Count);
        }
    }

    public async Task UpsertOverrideAsync(ConceptMapEntry entry, CancellationToken ct = default)
    {
        var filter = Builders<ConceptMapEntry>.Filter.And(
            Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceSystem, entry.SourceSystem),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.SourceCode, entry.SourceCode),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetSystem, entry.TargetSystem),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.TargetCode, entry.TargetCode),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.TenantId, entry.TenantId),
            Builders<ConceptMapEntry>.Filter.Eq(e => e.IsOverride, true)
        );

        var update = Builders<ConceptMapEntry>.Update
            .Set(e => e.SourceDisplay, entry.SourceDisplay)
            .Set(e => e.TargetDisplay, entry.TargetDisplay)
            .Set(e => e.Equivalence, entry.Equivalence)
            .Set(e => e.Priority, entry.Priority)
            .Set(e => e.Rule, entry.Rule)
            .Set(e => e.MapVersionId, entry.MapVersionId)
            .Set(e => e.IsOverride, true)
            .Set(e => e.MapGroupId, entry.MapGroupId)
            .SetOnInsert(e => e.SourceSystem, entry.SourceSystem)
            .SetOnInsert(e => e.SourceCode, entry.SourceCode)
            .SetOnInsert(e => e.TargetSystem, entry.TargetSystem)
            .SetOnInsert(e => e.TargetCode, entry.TargetCode)
            .SetOnInsert(e => e.TenantId, entry.TenantId);

        await _entries.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<MapVersion?> GetActiveMapVersionAsync(
        string sourceSystem, string targetSystem, CancellationToken ct = default)
    {
        return await _versions.Find(v =>
            v.SourceSystem == sourceSystem &&
            v.TargetSystem == targetSystem &&
            v.IsActive)
            .SortByDescending(v => v.ImportedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveMapVersionAsync(MapVersion version, CancellationToken ct = default)
    {
        await _versions.InsertOneAsync(version, cancellationToken: ct);
    }

    public async Task DeactivatePreviousVersionsAsync(string mapName, string exceptVersionId, CancellationToken ct = default)
    {
        var filter = Builders<MapVersion>.Filter.And(
            Builders<MapVersion>.Filter.Eq(v => v.MapName, mapName),
            Builders<MapVersion>.Filter.Ne(v => v.Id, exceptVersionId),
            Builders<MapVersion>.Filter.Eq(v => v.IsActive, true)
        );

        var update = Builders<MapVersion>.Update.Set(v => v.IsActive, false);
        await _versions.UpdateManyAsync(filter, update, cancellationToken: ct);
    }

    public async Task<List<MapVersion>> GetAllMapVersionsAsync(CancellationToken ct = default)
    {
        return await _versions.Find(_ => true)
            .SortByDescending(v => v.ImportedAt)
            .ToListAsync(ct);
    }
}
