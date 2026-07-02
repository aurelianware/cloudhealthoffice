using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// MongoDB implementation of ITenantEnrollmentConfigRepository.
///
/// Collection: enrollment_tenant_config
/// Index: _id  — TenantId is stored as the Mongo document id, so the
/// built-in unique _id index provides primary lookup + list ordering.
///
/// No TTL index — config documents are permanent until explicitly deleted or replaced.
/// </summary>
public sealed class TenantEnrollmentConfigRepositoryMongo : ITenantEnrollmentConfigRepository
{
    private readonly IMongoCollection<MongoTenantConfigDocument> _collection;
    private readonly ILogger<TenantEnrollmentConfigRepositoryMongo> _logger;

    public TenantEnrollmentConfigRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<TenantEnrollmentConfigRepositoryMongo> logger)
    {
        var collectionName = configuration["ProviderEnrollmentService:TenantConfigCollection"]
                             ?? "enrollment_tenant_config";

        _collection = database.GetCollection<MongoTenantConfigDocument>(collectionName);
        _logger     = logger;

        EnsureIndexes();
    }

    public async Task<TenantEnrollmentConfig?> GetAsync(
        string tenantId, CancellationToken ct = default)
    {
        var filter = Builders<MongoTenantConfigDocument>.Filter
            .Eq(d => d.TenantId, tenantId);

        var doc = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return doc?.ToModel();
    }

    public async Task UpsertAsync(
        TenantEnrollmentConfig config, CancellationToken ct = default)
    {
        var doc    = MongoTenantConfigDocument.FromModel(config);
        var filter = Builders<MongoTenantConfigDocument>.Filter
            .Eq(d => d.TenantId, config.TenantId);

        await _collection.ReplaceOneAsync(
            filter, doc,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken: ct);

        _logger.LogInformation(
            "TenantEnrollmentConfig upserted for tenant {TenantId} " +
            "with {LobOverrideCount} LOB overrides",
            config.TenantId, config.LobOverrides.Count);
    }

    public async Task DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        var filter = Builders<MongoTenantConfigDocument>.Filter
            .Eq(d => d.TenantId, tenantId);

        await _collection.DeleteOneAsync(filter, ct);

        _logger.LogInformation(
            "TenantEnrollmentConfig deleted for tenant {TenantId}", tenantId);
    }

    public async Task<IReadOnlyList<TenantEnrollmentConfig>> ListAsync(
        CancellationToken ct = default)
    {
        var sort = Builders<MongoTenantConfigDocument>.Sort
            .Ascending(d => d.TenantId);

        var docs = await _collection.Find(_ => true).Sort(sort).ToListAsync(ct);
        return docs.Select(d => d.ToModel()).ToList();
    }

    private void EnsureIndexes()
    {
        // TenantId is [BsonId], which MongoDB stores as _id. The built-in
        // _id index is already unique; creating another unique _id index is
        // rejected by MongoDB.
    }

    // ── Mongo document type ───────────────────────────────────────
    // Mirrors TenantEnrollmentConfigDocument but uses BsonId instead
    // of Cosmos's JsonPropertyName("id") convention.

    private sealed class MongoTenantConfigDocument
    {
        [BsonId]
        public string TenantId                          { get; set; } = string.Empty;
        public IReadOnlyList<string> EnabledStateCodes  { get; set; } = [];
        public string? CaqhOrganizationId               { get; set; }
        public string DefaultGateMode                   { get; set; } = "Enforce";
        public int DefaultRevalidationWarningDays       { get; set; } = 90;
        public bool DefaultGoldCardBypassesGate         { get; set; }
        public IReadOnlyList<string> McoIds             { get; set; } = [];
        public IReadOnlyList<MongoLobOverride> LobOverrides { get; set; } = [];
        public DateTime UpdatedAt                       { get; set; } = DateTime.UtcNow;

        public static MongoTenantConfigDocument FromModel(TenantEnrollmentConfig m) => new()
        {
            TenantId                        = m.TenantId,
            EnabledStateCodes               = m.EnabledStateCodes,
            CaqhOrganizationId              = m.CaqhOrganizationId,
            DefaultGateMode                 = m.DefaultGateMode.ToString(),
            DefaultRevalidationWarningDays  = m.DefaultRevalidationWarningDays,
            DefaultGoldCardBypassesGate     = m.DefaultGoldCardBypassesGate,
            McoIds                          = m.McoIds,
            LobOverrides                    = m.LobOverrides.Select(MongoLobOverride.FromModel).ToList(),
            UpdatedAt                       = DateTime.UtcNow
        };

        public TenantEnrollmentConfig ToModel() => new()
        {
            TenantId                        = TenantId,
            EnabledStateCodes               = EnabledStateCodes,
            CaqhOrganizationId              = CaqhOrganizationId,
            DefaultGateMode                 = Enum.Parse<EnrollmentGateMode>(DefaultGateMode),
            DefaultRevalidationWarningDays  = DefaultRevalidationWarningDays,
            DefaultGoldCardBypassesGate     = DefaultGoldCardBypassesGate,
            McoIds                          = McoIds,
            LobOverrides                    = LobOverrides.Select(o => o.ToModel()).ToList()
        };
    }

    private sealed class MongoLobOverride
    {
        public string Lob                               { get; set; } = string.Empty;
        public string? GateMode                         { get; set; }
        public IReadOnlyList<string>? EnabledStateCodes { get; set; }
        public int? RevalidationWarningDays             { get; set; }
        public bool? GoldCardBypassesGate               { get; set; }

        public static MongoLobOverride FromModel(LobEnrollmentOverride m) => new()
        {
            Lob                     = m.Lob.ToString(),
            GateMode                = m.GateMode?.ToString(),
            EnabledStateCodes       = m.EnabledStateCodes,
            RevalidationWarningDays = m.RevalidationWarningDays,
            GoldCardBypassesGate    = m.GoldCardBypassesGate
        };

        public LobEnrollmentOverride ToModel() => new()
        {
            Lob                     = Enum.Parse<LineOfBusiness>(Lob),
            GateMode                = GateMode is null ? null : Enum.Parse<EnrollmentGateMode>(GateMode),
            EnabledStateCodes       = EnabledStateCodes,
            RevalidationWarningDays = RevalidationWarningDays,
            GoldCardBypassesGate    = GoldCardBypassesGate
        };
    }
}
