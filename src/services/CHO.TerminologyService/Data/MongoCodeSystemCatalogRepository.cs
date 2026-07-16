using CHO.TerminologyService.Models;
using CHO.TerminologyService.Services;
using MongoDB.Driver;

namespace CHO.TerminologyService.Data;

public sealed class MongoCodeSystemCatalogRepository : ICodeSystemCatalogRepository
{
    private readonly IMongoCollection<CodeSystemConcept> _concepts;
    private readonly ILogger<MongoCodeSystemCatalogRepository> _logger;

    public MongoCodeSystemCatalogRepository(
        IMongoDatabase database,
        ILogger<MongoCodeSystemCatalogRepository> logger)
    {
        _concepts = database.GetCollection<CodeSystemConcept>("code_system_concepts");
        _logger = logger;
        EnsureIndexes();
    }

    public async Task<CodeSystemDisplay?> FindDisplayAsync(
        string system,
        string code,
        string? tenantId = null,
        CancellationToken ct = default)
    {
        var normalizedSystem = NormalizeSystem(system);
        var normalizedCode = NormalizeCode(code);
        if (string.IsNullOrWhiteSpace(normalizedSystem) || string.IsNullOrWhiteSpace(normalizedCode))
        {
            return null;
        }

        var tenantOverrideFilter = !string.IsNullOrWhiteSpace(tenantId)
            ? Builders<CodeSystemConcept>.Filter.And(
                Builders<CodeSystemConcept>.Filter.Eq(x => x.IsOverride, true),
                Builders<CodeSystemConcept>.Filter.Eq(x => x.TenantId, tenantId))
            : Builders<CodeSystemConcept>.Filter.Eq(x => x.Id, "__never_match__");

        var globalFilter = Builders<CodeSystemConcept>.Filter.And(
            Builders<CodeSystemConcept>.Filter.Eq(x => x.IsOverride, false),
            Builders<CodeSystemConcept>.Filter.Eq(x => x.TenantId, null));

        var filter = Builders<CodeSystemConcept>.Filter.And(
            Builders<CodeSystemConcept>.Filter.Eq(x => x.System, normalizedSystem),
            Builders<CodeSystemConcept>.Filter.Eq(x => x.Code, normalizedCode),
            Builders<CodeSystemConcept>.Filter.Ne(x => x.Display, null),
            Builders<CodeSystemConcept>.Filter.Ne(x => x.Display, string.Empty),
            Builders<CodeSystemConcept>.Filter.Or(tenantOverrideFilter, globalFilter));

        var concept = await _concepts.Find(filter)
            .Sort(Builders<CodeSystemConcept>.Sort
                .Descending(x => x.IsOverride)
                .Descending(x => x.UpdatedAtUtc))
            .FirstOrDefaultAsync(ct);

        return concept is null
            ? null
            : new CodeSystemDisplay(
                concept.Display,
                concept.Version,
                concept.IsOverride ? "CodeSystemOverride" : concept.Source);
    }

    public async Task UpsertManyAsync(IEnumerable<CodeSystemConcept> concepts, CancellationToken ct = default)
    {
        var writes = concepts
            .Select(Normalize)
            .Where(concept =>
                !string.IsNullOrWhiteSpace(concept.System) &&
                !string.IsNullOrWhiteSpace(concept.Code) &&
                !string.IsNullOrWhiteSpace(concept.Display))
            .Select(concept =>
            {
                var filter = Builders<CodeSystemConcept>.Filter.And(
                    Builders<CodeSystemConcept>.Filter.Eq(x => x.System, concept.System),
                    Builders<CodeSystemConcept>.Filter.Eq(x => x.Code, concept.Code),
                    Builders<CodeSystemConcept>.Filter.Eq(x => x.TenantId, concept.TenantId),
                    Builders<CodeSystemConcept>.Filter.Eq(x => x.IsOverride, concept.IsOverride));

                var update = Builders<CodeSystemConcept>.Update
                    .Set(x => x.Display, concept.Display)
                    .Set(x => x.Version, concept.Version)
                    .Set(x => x.Source, concept.Source)
                    .Set(x => x.UpdatedAtUtc, concept.UpdatedAtUtc)
                    .SetOnInsert(x => x.Id, concept.Id)
                    .SetOnInsert(x => x.System, concept.System)
                    .SetOnInsert(x => x.Code, concept.Code)
                    .SetOnInsert(x => x.TenantId, concept.TenantId)
                    .SetOnInsert(x => x.IsOverride, concept.IsOverride);

                return new UpdateOneModel<CodeSystemConcept>(filter, update)
                {
                    IsUpsert = true
                };
            })
            .Cast<WriteModel<CodeSystemConcept>>()
            .ToList();

        if (writes.Count == 0)
        {
            return;
        }

        await _concepts.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
        _logger.LogInformation("Upserted {ConceptCount} code-system concepts", writes.Count);
    }

    private void EnsureIndexes()
    {
        _concepts.Indexes.CreateOne(new CreateIndexModel<CodeSystemConcept>(
            Builders<CodeSystemConcept>.IndexKeys
                .Ascending(x => x.System)
                .Ascending(x => x.Code)
                .Ascending(x => x.TenantId)
                .Ascending(x => x.IsOverride),
            new CreateIndexOptions
            {
                Name = "idx_code_system_lookup",
                Unique = true
            }));

        _logger.LogInformation("MongoDB indexes ensured for code_system_concepts");
    }

    private static CodeSystemConcept Normalize(CodeSystemConcept concept)
    {
        var normalizedSystem = NormalizeSystem(concept.System);
        var normalizedCode = NormalizeCode(concept.Code);
        return new CodeSystemConcept
        {
            Id = string.IsNullOrWhiteSpace(concept.Id)
                ? BuildId(normalizedSystem, normalizedCode, concept.TenantId, concept.IsOverride)
                : concept.Id,
            System = normalizedSystem,
            Code = normalizedCode,
            Display = concept.Display.Trim(),
            Version = string.IsNullOrWhiteSpace(concept.Version) ? null : concept.Version.Trim(),
            Source = string.IsNullOrWhiteSpace(concept.Source) ? "CodeSystemCatalog" : concept.Source.Trim(),
            TenantId = string.IsNullOrWhiteSpace(concept.TenantId) ? null : concept.TenantId.Trim(),
            IsOverride = concept.IsOverride,
            UpdatedAtUtc = concept.UpdatedAtUtc == default ? DateTime.UtcNow : concept.UpdatedAtUtc
        };
    }

    private static string NormalizeSystem(string system) => system.Trim();

    private static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();

    private static string BuildId(string system, string code, string? tenantId, bool isOverride)
    {
        var scope = isOverride ? tenantId ?? "override" : "global";
        return $"{system}|{code}|{scope}";
    }
}
