using BenefitPlanService.Models;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Services;
using Microsoft.Azure.Cosmos;

namespace BenefitPlanService.Repositories;

/// <summary>
/// Cosmos-backed storage for service-category mappings (capability BP 5.6 —
/// Service Category Mapping). One document per
/// <c>(tenantId, benefitPlanId?, serviceTypeCode)</c> with the matching
/// <see cref="ProcedureCodeRule"/> list embedded; partition key is
/// <c>tenantId</c>.
///
/// <para>
/// The class implements three seams:
/// <list type="bullet">
///   <item><see cref="IServiceCategoryMappingRepository"/> — adjudication
///     read path (BenefitEngine class library).</item>
///   <item><see cref="IServiceCategoryMappingWriteRepository"/> — admin
///     write path.</item>
///   <item><see cref="ISystemDefaultsAppliedRecordRepository"/> — the
///     seeder's per-tenant idempotency record, stored as a sibling
///     document with <c>documentType="system-defaults-applied"</c> so the
///     mappings query naturally excludes it.</item>
/// </list>
/// In-process caching with write-invalidation is layered on top via
/// <see cref="CachingServiceCategoryMappingRepository"/>; this class is the
/// raw storage backend and contains no cache logic.
/// </para>
///
/// <para>
/// See <c>docs/architecture/service-category-mapping.md</c> for the
/// canonical resolution flow and the documented incoherence between
/// <c>Benefit.ServiceCategory</c> (free-text plan-author label) and
/// <c>ServiceTypeCode</c> (resolver output).
/// </para>
/// </summary>
public sealed class ChoServiceCategoryMappingRepository :
    IServiceCategoryMappingRepository,
    IServiceCategoryMappingWriteRepository,
    ISystemDefaultsAppliedRecordRepository
{
    public const string DefaultContainerName = "ServiceCategoryMappings";
    public const string DocumentTypeMapping = "mapping";
    public const string DocumentTypeSystemDefaultsApplied = "system-defaults-applied";

    private readonly Container _container;
    private readonly ILogger<ChoServiceCategoryMappingRepository> _logger;

    public ChoServiceCategoryMappingRepository(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        ILogger<ChoServiceCategoryMappingRepository> logger)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"]
            ?? throw new InvalidOperationException("CosmosDb:DatabaseName is required.");
        var containerName = configuration["CosmosDb:ServiceCategoryMappingsContainerName"]
            ?? DefaultContainerName;
        _container = cosmosClient.GetContainer(databaseName, containerName);
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
        try
        {
            var response = await _container.ReadItemAsync<MappingDocument>(
                id.ToString(),
                new PartitionKey(tenantId),
                cancellationToken: ct);
            var doc = response.Resource;
            return doc?.TenantId == tenantId ? doc.ToEntity() : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
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
        var response = await _container.CreateItemAsync(
            doc, new PartitionKey(mapping.TenantId), cancellationToken: ct);
        return response.Resource.ToEntity();
    }

    public async Task<ServiceCategoryMapping> UpdateAsync(
        ServiceCategoryMapping mapping, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(mapping.TenantId, mapping.Id, ct)
            ?? throw new KeyNotFoundException(
                $"ServiceCategoryMapping {mapping.Id} not found for tenant {mapping.TenantId}.");

        var doc = MappingDocument.From(mapping);
        var response = await _container.ReplaceItemAsync(
            doc, doc.Id, new PartitionKey(mapping.TenantId), cancellationToken: ct);
        return response.Resource.ToEntity();
    }

    public async Task<bool> DeleteAsync(
        string tenantId, Guid id, CancellationToken ct = default)
    {
        var existing = await GetByIdAsync(tenantId, id, ct);
        if (existing is null) return false;

        try
        {
            await _container.DeleteItemAsync<MappingDocument>(
                id.ToString(),
                new PartitionKey(tenantId),
                cancellationToken: ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<ServiceCategoryMapping>> ListAsync(
        string tenantId, Guid? benefitPlanId, CancellationToken ct = default)
    {
        // Tenant defaults: BenefitPlanId is unset on the document. Plan
        // overrides: BenefitPlanId equals the supplied value. The
        // <c>documentType</c> filter excludes the seeder's
        // SystemDefaultsApplied sibling document. Newest-first ordering
        // ensures the resolver's first-match-wins iteration prefers a
        // freshly seeded row over an older row for the same serviceTypeCode
        // (deterministic resolution after seeder version-bump re-apply).
        QueryDefinition query;
        if (benefitPlanId is null)
        {
            query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId " +
                "AND c.documentType = @docType " +
                "AND (NOT IS_DEFINED(c.benefitPlanId) OR c.benefitPlanId = null) " +
                "ORDER BY c.createdAt DESC")
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@docType", DocumentTypeMapping);
        }
        else
        {
            query = new QueryDefinition(
                "SELECT * FROM c WHERE c.tenantId = @tenantId " +
                "AND c.documentType = @docType " +
                "AND c.benefitPlanId = @planId " +
                "ORDER BY c.createdAt DESC")
                .WithParameter("@tenantId", tenantId)
                .WithParameter("@docType", DocumentTypeMapping)
                .WithParameter("@planId", benefitPlanId.Value.ToString());
        }

        var requestOptions = new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(tenantId)
        };
        var iterator = _container.GetItemQueryIterator<MappingDocument>(query, requestOptions: requestOptions);

        var results = new List<ServiceCategoryMapping>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(ct);
            foreach (var doc in page)
            {
                results.Add(doc.ToEntity());
            }
        }
        return results;
    }

    // ── ISystemDefaultsAppliedRecordRepository ──────────────────────────────

    public async Task<SystemDefaultsAppliedRecord?> GetAsync(
        string tenantId, CancellationToken ct = default)
    {
        var docId = AppliedRecordDocId(tenantId);
        try
        {
            var response = await _container.ReadItemAsync<AppliedRecordDocument>(
                docId,
                new PartitionKey(tenantId),
                cancellationToken: ct);
            return response.Resource?.ToEntity();
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task UpsertAsync(SystemDefaultsAppliedRecord record, CancellationToken ct = default)
    {
        var doc = AppliedRecordDocument.From(record);
        await _container.UpsertItemAsync(
            doc, new PartitionKey(record.TenantId), cancellationToken: ct);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string AppliedRecordDocId(string tenantId)
        => $"system-defaults-applied:{tenantId}";

    /// <summary>
    /// Storage shape: adds <c>documentType</c> and serializes
    /// <c>BenefitPlanId</c>/<c>Id</c> as strings so Cosmos partitions and
    /// indexes them as scalar values rather than nested objects.
    /// </summary>
    internal sealed class MappingDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = default!;

        [System.Text.Json.Serialization.JsonPropertyName("tenantId")]
        public string TenantId { get; set; } = default!;

        [System.Text.Json.Serialization.JsonPropertyName("documentType")]
        public string DocumentType { get; set; } = DocumentTypeMapping;

        [System.Text.Json.Serialization.JsonPropertyName("benefitPlanId")]
        public string? BenefitPlanId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("serviceTypeCode")]
        public string ServiceTypeCode { get; set; } = default!;

        [System.Text.Json.Serialization.JsonPropertyName("serviceTypeDescription")]
        public string ServiceTypeDescription { get; set; } = default!;

        [System.Text.Json.Serialization.JsonPropertyName("rules")]
        public List<ProcedureCodeRule> Rules { get; set; } = [];

        [System.Text.Json.Serialization.JsonPropertyName("effectiveStart")]
        public DateOnly? EffectiveStart { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("effectiveEnd")]
        public DateOnly? EffectiveEnd { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("isActive")]
        public bool IsActive { get; set; } = true;

        [System.Text.Json.Serialization.JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        public static MappingDocument From(ServiceCategoryMapping m) => new()
        {
            Id = m.Id.ToString(),
            TenantId = m.TenantId,
            BenefitPlanId = m.BenefitPlanId?.ToString(),
            ServiceTypeCode = m.ServiceTypeCode,
            ServiceTypeDescription = m.ServiceTypeDescription,
            Rules = m.Rules,
            EffectiveStart = m.EffectiveStart,
            EffectiveEnd = m.EffectiveEnd,
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
            Rules = Rules ?? [],
            EffectiveStart = EffectiveStart,
            EffectiveEnd = EffectiveEnd,
            IsActive = IsActive,
            CreatedAt = CreatedAt,
        };
    }

    internal sealed class AppliedRecordDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = default!;

        [System.Text.Json.Serialization.JsonPropertyName("tenantId")]
        public string TenantId { get; set; } = default!;

        [System.Text.Json.Serialization.JsonPropertyName("documentType")]
        public string DocumentType { get; set; } = DocumentTypeSystemDefaultsApplied;

        [System.Text.Json.Serialization.JsonPropertyName("appliedSeedVersion")]
        public int AppliedSeedVersion { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("appliedAt")]
        public DateTimeOffset AppliedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mappingCount")]
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
