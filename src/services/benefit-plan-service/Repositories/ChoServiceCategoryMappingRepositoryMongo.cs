using System.Globalization;
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
        if (mapping.CreatedAt == default)
        {
            mapping.CreatedAt = DateTimeOffset.UtcNow;
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

        // Newest-first ordering matches the Cosmos backend so the resolver's
        // first-match-wins iteration prefers freshly seeded rows over older
        // rows for the same serviceTypeCode after a seeder version-bump.
        var docs = await _mappings.Find(filter)
            .SortByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        return docs.Select(d => d.ToEntity()).ToList();
    }

    // ── ISystemDefaultsAppliedRecordRepository ──────────────────────────────

    public async Task<SystemDefaultsAppliedRecord?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        // Filter on the deterministic _id (`system-defaults-applied:{tenant}`)
        // alongside tenantId+documentType. The _id alone is sufficient for
        // uniqueness; the extra predicates defend against collisions if a
        // future schema change re-uses the id format.
        var b = Builders<AppliedRecordDocument>.Filter;
        var filter = b.And(
            b.Eq(x => x.Id, AppliedRecordDocId(tenantId)),
            b.Eq(x => x.TenantId, tenantId),
            b.Eq(x => x.DocumentType, DocumentTypeSystemDefaultsApplied));
        var doc = await _appliedRecords.Find(filter).FirstOrDefaultAsync(ct);
        return doc?.ToEntity();
    }

    public async Task UpsertAsync(SystemDefaultsAppliedRecord record, CancellationToken ct = default)
    {
        // Filter must include _id so ReplaceOneAsync targets the correct
        // document and Mongo doesn't refuse the replace because the
        // upserted document's _id differs from a tenant+documentType match
        // resolved by an alternative filter.
        var b = Builders<AppliedRecordDocument>.Filter;
        var docId = AppliedRecordDocId(record.TenantId);
        var filter = b.And(
            b.Eq(x => x.Id, docId),
            b.Eq(x => x.TenantId, record.TenantId),
            b.Eq(x => x.DocumentType, DocumentTypeSystemDefaultsApplied));
        var doc = AppliedRecordDocument.From(record);
        await _appliedRecords.ReplaceOneAsync(
            filter, doc, new ReplaceOptions { IsUpsert = true }, ct);
    }

    private static string AppliedRecordDocId(string tenantId)
        => $"system-defaults-applied:{tenantId}";

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
        public List<ProcedureCodeRuleDocument> Rules { get; set; } = [];

        // DateOnly persisted as ISO-yyyy-MM-dd string for cross-driver
        // portability. The Mongo BSON DateOnly serializer support is patchy
        // across driver versions; string is unambiguous.
        [BsonElement("effectiveStart")]
        public string? EffectiveStart { get; set; }

        [BsonElement("effectiveEnd")]
        public string? EffectiveEnd { get; set; }

        [BsonElement("isActive")]
        public bool IsActive { get; set; } = true;

        [BsonElement("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        public static MappingDocument From(ServiceCategoryMapping m) => new()
        {
            Id = m.Id.ToString(),
            TenantId = m.TenantId,
            BenefitPlanId = m.BenefitPlanId?.ToString(),
            ServiceTypeCode = m.ServiceTypeCode,
            ServiceTypeDescription = m.ServiceTypeDescription,
            Rules = m.Rules.Select(ProcedureCodeRuleDocument.From).ToList(),
            EffectiveStart = m.EffectiveStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EffectiveEnd = m.EffectiveEnd?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IsActive = m.IsActive,
            CreatedAt = m.CreatedAt,
        };

        public ServiceCategoryMapping ToEntity() => new()
        {
            Id = Guid.Parse(Id),
            TenantId = TenantId,
            BenefitPlanId = string.IsNullOrEmpty(BenefitPlanId) ? null : Guid.Parse(BenefitPlanId),
            ServiceTypeCode = ServiceTypeCode,
            ServiceTypeDescription = ServiceTypeDescription,
            Rules = Rules?.Select(r => r.ToEntity()).ToList() ?? [],
            EffectiveStart = ParseDateOrNull(EffectiveStart),
            EffectiveEnd = ParseDateOrNull(EffectiveEnd),
            IsActive = IsActive,
            CreatedAt = CreatedAt,
        };

        // ParseExact + InvariantCulture ensures the persisted ISO-8601
        // string round-trips identically regardless of the host's current
        // culture. DateOnly.Parse without an explicit provider is
        // culture-sensitive and can fail or silently misparse in cultures
        // where '-' is not the canonical date separator.
        private static DateOnly? ParseDateOrNull(string? s)
            => string.IsNullOrEmpty(s)
                ? null
                : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    [BsonIgnoreExtraElements]
    internal sealed class ProcedureCodeRuleDocument
    {
        [BsonElement("id")]
        public string Id { get; set; } = default!;

        [BsonElement("priority")]
        public int Priority { get; set; }

        [BsonElement("codeType")]
        public string CodeType { get; set; } = "CPT";

        [BsonElement("codePattern")]
        public string CodePattern { get; set; } = default!;

        [BsonElement("codeRangeEnd")]
        public string? CodeRangeEnd { get; set; }

        [BsonElement("placeOfServiceCode")]
        public string? PlaceOfServiceCode { get; set; }

        [BsonElement("requiredModifier")]
        public string? RequiredModifier { get; set; }

        [BsonElement("revenueCode")]
        public string? RevenueCode { get; set; }

        public static ProcedureCodeRuleDocument From(ProcedureCodeRule r) => new()
        {
            Id = (r.Id == Guid.Empty ? Guid.NewGuid() : r.Id).ToString(),
            Priority = r.Priority,
            CodeType = r.CodeType,
            CodePattern = r.CodePattern,
            CodeRangeEnd = r.CodeRangeEnd,
            PlaceOfServiceCode = r.PlaceOfServiceCode,
            RequiredModifier = r.RequiredModifier,
            RevenueCode = r.RevenueCode,
        };

        public ProcedureCodeRule ToEntity() => new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid() : Guid.Parse(Id),
            Priority = Priority,
            CodeType = CodeType,
            CodePattern = CodePattern,
            CodeRangeEnd = CodeRangeEnd,
            PlaceOfServiceCode = PlaceOfServiceCode,
            RequiredModifier = RequiredModifier,
            RevenueCode = RevenueCode,
        };
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
