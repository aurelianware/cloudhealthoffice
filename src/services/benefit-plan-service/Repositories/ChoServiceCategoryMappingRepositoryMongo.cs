using BenefitPlanService.Models;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace BenefitPlanService.Repositories;

/// <summary>
/// MongoDB-backed equivalent of <see cref="ChoServiceCategoryMappingRepository"/>
/// (capability BP 5.6 — Service Category Mapping).
///
/// <para>
/// Implements the same three seams (read, write, applied-record) against an
/// <c>IMongoCollection</c>. Documents are tagged with a <c>documentType</c>
/// discriminator so the mappings collection can host both the
/// <c>ServiceCategoryMapping</c> rows and the seeder's
/// <c>SystemDefaultsApplied</c> idempotency rows in one collection without
/// either query reading the other's documents. Caching is layered on by
/// <see cref="CachingServiceCategoryMappingRepository"/>; this class is the
/// raw storage backend.
/// </para>
/// </summary>
public sealed class ChoServiceCategoryMappingRepositoryMongo :
    IServiceCategoryMappingRepository,
    IServiceCategoryMappingWriteRepository,
    ISystemDefaultsAppliedRecordRepository
{
    public const string DocumentTypeMapping = "mapping";
    public const string DocumentTypeSystemDefaultsApplied = "system-defaults-applied";

    private readonly IMongoCollection<MappingDocument> _mappings;
    private readonly IMongoCollection<AppliedRecordDocument> _appliedRecords;
    private readonly ILogger<ChoServiceCategoryMappingRepositoryMongo> _logger;

    public ChoServiceCategoryMappingRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<ChoServiceCategoryMappingRepositoryMongo> logger)
    {
        // Container-name config key intentionally mirrors Cosmos so a tenant
        // migrating between backends sees identical config keys.
        var collectionName = configuration["CosmosDb:ServiceCategoryMappingsContainerName"]
            ?? ChoServiceCategoryMappingRepository.DefaultContainerName;
        _mappings = database.GetCollection<MappingDocument>(collectionName);
        _appliedRecords = database.GetCollection<AppliedRecordDocument>(collectionName);
        _logger = logger;
    }

    // ── IServiceCategoryMappingRepository (read seam) ───────────────────────

    public Task<IReadOnlyList<ServiceCategoryMapping>> GetMappingsAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
        => ListAsync(tenantId, benefitPlanId, ct);

    // ── IServiceCategoryMappingWriteRepository ──────────────────────────────

    public async Task<ServiceCategoryMapping?> GetByIdAsync(
        string tenantId, Guid id, CancellationToken ct = default)
    {
        var b = Builders<MappingDocument>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.DocumentType, DocumentTypeMapping),
            b.Eq(x => x.Id, id.ToString()));
        var doc = await _mappings.Find(filter).FirstOrDefaultAsync(ct);
        return doc?.ToEntity();
    }

    public async Task<ServiceCategoryMapping> CreateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        if (mapping.Id == Guid.Empty)
        {
            mapping.Id = Guid.NewGuid();
        }
        var doc = MappingDocument.From(mapping);
        await _mappings.InsertOneAsync(doc, cancellationToken: ct);
        return doc.ToEntity();
    }

    public async Task<ServiceCategoryMapping> UpdateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        _ = await GetByIdAsync(mapping.TenantId, mapping.Id, ct)
            ?? throw new KeyNotFoundException(
                $"ServiceCategoryMapping {mapping.Id} not found for tenant {mapping.TenantId}.");

        var b = Builders<MappingDocument>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, mapping.TenantId),
            b.Eq(x => x.DocumentType, DocumentTypeMapping),
            b.Eq(x => x.Id, mapping.Id.ToString()));
        var doc = MappingDocument.From(mapping);
        await _mappings.ReplaceOneAsync(filter, doc, cancellationToken: ct);
        return doc.ToEntity();
    }

    public async Task<bool> DeleteAsync(
        string tenantId, Guid id, CancellationToken ct = default)
    {
        var b = Builders<MappingDocument>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.DocumentType, DocumentTypeMapping),
            b.Eq(x => x.Id, id.ToString()));
        var result = await _mappings.DeleteOneAsync(filter, ct);
        return result.DeletedCount > 0;
    }

    public async Task<IReadOnlyList<ServiceCategoryMapping>> ListAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        var b = Builders<MappingDocument>.Filter;
        FilterDefinition<MappingDocument> planFilter = benefitPlanId is null
            ? b.Or(
                b.Eq(x => x.BenefitPlanId, null),
                b.Exists(x => x.BenefitPlanId, false))
            : b.Eq(x => x.BenefitPlanId, benefitPlanId.Value.ToString());

        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.DocumentType, DocumentTypeMapping),
            planFilter);

        var docs = await _mappings.Find(filter).ToListAsync(ct);
        return docs.Select(d => d.ToEntity()).ToList();
    }

    // ── ISystemDefaultsAppliedRecordRepository ──────────────────────────────

    public async Task<SystemDefaultsAppliedRecord?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        var b = Builders<AppliedRecordDocument>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.DocumentType, DocumentTypeSystemDefaultsApplied));
        var doc = await _appliedRecords.Find(filter).FirstOrDefaultAsync(ct);
        return doc?.ToEntity();
    }

    public async Task UpsertAsync(SystemDefaultsAppliedRecord record, CancellationToken ct = default)
    {
        var b = Builders<AppliedRecordDocument>.Filter;
        var filter = b.And(
            b.Eq(x => x.TenantId, record.TenantId),
            b.Eq(x => x.DocumentType, DocumentTypeSystemDefaultsApplied));
        var doc = AppliedRecordDocument.From(record);
        await _appliedRecords.ReplaceOneAsync(
            filter, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Mongo storage document. Uses string Id (Guid as text) and string
    /// BenefitPlanId so Mongo indexes them as scalar values; <c>documentType</c>
    /// discriminates from the seeder's applied-record document.
    /// </summary>
    [BsonIgnoreExtraElements]
    internal sealed class MappingDocument
    {
        [BsonId]
        [BsonElement("_id")]
        public string Id { get; set; } = default!;

        [BsonElement("tenantId")]
        public string TenantId { get; set; } = default!;

        [BsonElement("documentType")]
        public string DocumentType { get; set; } = DocumentTypeMapping;

        [BsonElement("benefitPlanId")]
        public string? BenefitPlanId { get; set; }

        [BsonElement("serviceTypeCode")]
        public string ServiceTypeCode { get; set; } = default!;

        [BsonElement("serviceTypeDescription")]
        public string ServiceTypeDescription { get; set; } = default!;

        [BsonElement("rules")]
        public List<ProcedureCodeRule> Rules { get; set; } = [];

        // DateOnly persisted as ISO-yyyy-MM-dd string for cross-driver
        // portability. The Mongo BSON DateOnly serializer support is patchy
        // across driver versions; string is unambiguous.
        [BsonElement("effectiveStart")]
        public string? EffectiveStart { get; set; }

        [BsonElement("effectiveEnd")]
        public string? EffectiveEnd { get; set; }

        [BsonElement("isActive")]
        public bool IsActive { get; set; } = true;

        public static MappingDocument From(ServiceCategoryMapping m) => new()
        {
            Id = m.Id.ToString(),
            TenantId = m.TenantId,
            BenefitPlanId = m.BenefitPlanId?.ToString(),
            ServiceTypeCode = m.ServiceTypeCode,
            ServiceTypeDescription = m.ServiceTypeDescription,
            Rules = m.Rules,
            EffectiveStart = m.EffectiveStart?.ToString("yyyy-MM-dd"),
            EffectiveEnd = m.EffectiveEnd?.ToString("yyyy-MM-dd"),
            IsActive = m.IsActive,
        };

        public ServiceCategoryMapping ToEntity() => new()
        {
            Id = Guid.Parse(Id),
            TenantId = TenantId,
            BenefitPlanId = string.IsNullOrEmpty(BenefitPlanId) ? null : Guid.Parse(BenefitPlanId),
            ServiceTypeCode = ServiceTypeCode,
            ServiceTypeDescription = ServiceTypeDescription,
            Rules = Rules ?? [],
            EffectiveStart = ParseDateOrNull(EffectiveStart),
            EffectiveEnd = ParseDateOrNull(EffectiveEnd),
            IsActive = IsActive,
        };

        private static DateOnly? ParseDateOrNull(string? s)
            => string.IsNullOrEmpty(s) ? null : DateOnly.Parse(s);
    }

    [BsonIgnoreExtraElements]
    internal sealed class AppliedRecordDocument
    {
        [BsonId]
        [BsonElement("_id")]
        public string Id { get; set; } = default!;

        [BsonElement("tenantId")]
        public string TenantId { get; set; } = default!;

        [BsonElement("documentType")]
        public string DocumentType { get; set; } = DocumentTypeSystemDefaultsApplied;

        [BsonElement("appliedSeedVersion")]
        public int AppliedSeedVersion { get; set; }

        [BsonElement("appliedAt")]
        public DateTimeOffset AppliedAt { get; set; }

        [BsonElement("mappingCount")]
        public int MappingCount { get; set; }

        public static AppliedRecordDocument From(SystemDefaultsAppliedRecord r) => new()
        {
            Id = $"system-defaults-applied:{r.TenantId}",
            TenantId = r.TenantId,
            AppliedSeedVersion = r.AppliedSeedVersion,
            AppliedAt = r.AppliedAt,
            MappingCount = r.MappingCount,
        };

        public SystemDefaultsAppliedRecord ToEntity() => new()
        {
            Id = Id,
            TenantId = TenantId,
            AppliedSeedVersion = AppliedSeedVersion,
            AppliedAt = AppliedAt,
            MappingCount = MappingCount,
        };
    }
}
