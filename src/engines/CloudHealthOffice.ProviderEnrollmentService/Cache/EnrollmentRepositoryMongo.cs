using CloudHealthOffice.ProviderEnrollmentService.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CloudHealthOffice.ProviderEnrollmentService.Cache;

/// <summary>
/// MongoDB implementation of IEnrollmentRepository.
///
/// Collection: enrollment_cache
/// Indexes:
///   { npi: 1, stateCode: 1 }   unique compound  — primary lookup key
///   { stateCode: 1, status: 1 } — panel queries by state + status
///   { revalidationDueDate: 1 }  — revalidation alert queries
///   { mcoParticipation: 1 }     — MCO panel reconciliation
///   { cachedAt: 1 }             TTL index (expireAfterSeconds = CacheTtl)
/// </summary>
public sealed class EnrollmentRepositoryMongo : IEnrollmentRepository
{
    private readonly IMongoCollection<MongoEnrollmentDocument> _collection;
    private readonly TimeSpan _cacheTtl;
    private readonly ILogger<EnrollmentRepositoryMongo> _logger;

    public EnrollmentRepositoryMongo(
        IMongoDatabase database,
        IOptions<ProviderEnrollmentOptions> options,
        ILogger<EnrollmentRepositoryMongo> logger)
    {
        _cacheTtl   = options.Value.CacheTtl;
        _logger     = logger;
        _collection = database.GetCollection<MongoEnrollmentDocument>("enrollment_cache");

        EnsureIndexes();
    }

    // ── IEnrollmentRepository ─────────────────────────────────────

    public async Task<StateEnrollmentRecord?> GetAsync(
        string npi, string stateCode, CancellationToken ct = default)
    {
        var filter = Builders<MongoEnrollmentDocument>.Filter.And(
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Npi, npi),
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.StateCode, stateCode));

        var doc = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return doc?.ToRecord();
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetAllStatesAsync(
        string npi, CancellationToken ct = default)
    {
        var filter = Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Npi, npi);
        var docs   = await _collection.Find(filter).ToListAsync(ct);
        return docs.Select(d => d.ToRecord()).ToList();
    }

    public async Task UpsertAsync(StateEnrollmentRecord record, CancellationToken ct = default)
    {
        var doc = MongoEnrollmentDocument.FromRecord(record);
        var filter = Builders<MongoEnrollmentDocument>.Filter.And(
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Npi, record.Npi),
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.StateCode, record.StateCode));

        await _collection.ReplaceOneAsync(filter, doc,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken: ct);
    }

    public async Task BulkUpsertAsync(
        IEnumerable<StateEnrollmentRecord> records, CancellationToken ct = default)
    {
        var ops = records.Select(r =>
        {
            var doc    = MongoEnrollmentDocument.FromRecord(r);
            var filter = Builders<MongoEnrollmentDocument>.Filter.And(
                Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Npi, r.Npi),
                Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.StateCode, r.StateCode));
            return new ReplaceOneModel<MongoEnrollmentDocument>(filter, doc) { IsUpsert = true };
        }).ToList();

        if (ops.Count == 0) return;

        var result = await _collection.BulkWriteAsync(ops, new BulkWriteOptions { IsOrdered = false }, ct);
        _logger.LogDebug("Bulk upsert: {Upserted} upserted, {Modified} modified",
            result.Upserts.Count, result.ModifiedCount);
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetProvidersWithRevalidationDueSoonAsync(
        int withinDays, string? stateCode = null, CancellationToken ct = default)
    {
        var today   = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(withinDays).ToString("O");

        var filters = new List<FilterDefinition<MongoEnrollmentDocument>>
        {
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Status, "Active"),
            Builders<MongoEnrollmentDocument>.Filter.Gte(d => d.RevalidationDueDate, today.ToString("O")),
            Builders<MongoEnrollmentDocument>.Filter.Lte(d => d.RevalidationDueDate, horizon)
        };

        if (stateCode is not null)
            filters.Add(Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.StateCode, stateCode));

        var docs = await _collection.Find(Builders<MongoEnrollmentDocument>.Filter.And(filters))
            .ToListAsync(ct);

        return docs.Select(d => d.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<StateEnrollmentRecord>> GetActivePanelByMcoAsync(
        string stateCode, string mcoId, CancellationToken ct = default)
    {
        var filter = Builders<MongoEnrollmentDocument>.Filter.And(
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.StateCode, stateCode),
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Status, "Active"),
            Builders<MongoEnrollmentDocument>.Filter.AnyEq(d => d.McoParticipation, mcoId));

        var docs = await _collection.Find(filter).ToListAsync(ct);
        return docs.Select(d => d.ToRecord()).ToList();
    }

    public async Task DeleteAsync(string npi, string stateCode, CancellationToken ct = default)
    {
        var filter = Builders<MongoEnrollmentDocument>.Filter.And(
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.Npi, npi),
            Builders<MongoEnrollmentDocument>.Filter.Eq(d => d.StateCode, stateCode));

        await _collection.DeleteOneAsync(filter, ct);
    }

    // ── Index setup ───────────────────────────────────────────────

    private void EnsureIndexes()
    {
        var indexes = new List<CreateIndexModel<MongoEnrollmentDocument>>
        {
            // Primary lookup — unique compound
            new(Builders<MongoEnrollmentDocument>.IndexKeys
                .Ascending(d => d.Npi)
                .Ascending(d => d.StateCode),
                new CreateIndexOptions { Unique = true }),

            // Panel queries by state + status
            new(Builders<MongoEnrollmentDocument>.IndexKeys
                .Ascending(d => d.StateCode)
                .Ascending(d => d.Status)),

            // Revalidation alert queries
            new(Builders<MongoEnrollmentDocument>.IndexKeys
                .Ascending(d => d.RevalidationDueDate)),

            // MCO panel reconciliation
            new(Builders<MongoEnrollmentDocument>.IndexKeys
                .Ascending(d => d.McoParticipation)),

            // TTL — auto-expire documents based on CacheTtl
            new(Builders<MongoEnrollmentDocument>.IndexKeys
                .Ascending(d => d.CachedAt),
                new CreateIndexOptions { ExpireAfter = _cacheTtl })
        };

        _collection.Indexes.CreateMany(indexes);
    }

    // ── Mongo document type ───────────────────────────────────────

    private sealed class MongoEnrollmentDocument
    {
        [BsonId]
        public ObjectId Id              { get; set; }
        public string Npi               { get; set; } = string.Empty;
        public string StateCode         { get; set; } = string.Empty;
        public string SourceSystem      { get; set; } = string.Empty;
        public string Status            { get; set; } = string.Empty;
        public string EffectiveDate     { get; set; } = string.Empty;
        public string? TerminationDate  { get; set; }
        public string? RevalidationDueDate { get; set; }
        public string? LastVerifiedDate { get; set; }
        public string ProviderType      { get; set; } = string.Empty;
        public int SupportedLobs        { get; set; }
        public List<string> EnrolledTaxonomies  { get; set; } = [];
        public List<string> EnrolledCounties    { get; set; } = [];
        public List<string> EnrolledZipCodes    { get; set; } = [];
        public List<string> McoParticipation    { get; set; } = [];
        public List<RestrictionDocument> Restrictions { get; set; } = [];
        public DateTime CachedAt        { get; set; }

        public static MongoEnrollmentDocument FromRecord(StateEnrollmentRecord r) => new()
        {
            Npi                 = r.Npi,
            StateCode           = r.StateCode,
            SourceSystem        = r.SourceSystem,
            Status              = r.Status.ToString(),
            EffectiveDate       = r.EffectiveDate.ToString("O"),
            TerminationDate     = r.TerminationDate?.ToString("O"),
            RevalidationDueDate = r.RevalidationDueDate?.ToString("O"),
            LastVerifiedDate    = r.LastVerifiedDate?.ToString("O"),
            ProviderType        = r.ProviderType.ToString(),
            SupportedLobs       = (int)r.SupportedLobs,
            EnrolledTaxonomies  = r.EnrolledTaxonomies.ToList(),
            EnrolledCounties    = r.EnrolledCounties.ToList(),
            EnrolledZipCodes    = r.EnrolledZipCodes.ToList(),
            McoParticipation    = r.McoParticipation.ToList(),
            Restrictions        = r.Restrictions.Select(RestrictionDocument.From).ToList(),
            CachedAt            = DateTime.UtcNow
        };

        public StateEnrollmentRecord ToRecord() => new()
        {
            Npi                 = Npi,
            StateCode           = StateCode,
            SourceSystem        = SourceSystem,
            Status              = Enum.Parse<EnrollmentStatus>(Status),
            EffectiveDate       = DateOnly.Parse(EffectiveDate),
            TerminationDate     = TerminationDate is null ? null : DateOnly.Parse(TerminationDate),
            RevalidationDueDate = RevalidationDueDate is null ? null : DateOnly.Parse(RevalidationDueDate),
            LastVerifiedDate    = LastVerifiedDate is null ? null : DateOnly.Parse(LastVerifiedDate),
            ProviderType        = Enum.Parse<ProviderTypeClassification>(ProviderType),
            SupportedLobs       = (LineOfBusiness)SupportedLobs,
            EnrolledTaxonomies  = EnrolledTaxonomies,
            EnrolledCounties    = EnrolledCounties,
            EnrolledZipCodes    = EnrolledZipCodes,
            McoParticipation    = McoParticipation,
            Restrictions        = Restrictions.Select(r => r.ToRestriction()).ToList(),
            CachedAt            = CachedAt,
            IsFromCache         = true
        };
    }
}
