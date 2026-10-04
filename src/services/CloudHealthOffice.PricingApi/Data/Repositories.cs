using CloudHealthOffice.PricingApi.Models;
using CloudHealthOffice.PricingApi.Security;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.PricingApi.Data;

// ─────────────────────────────────────────────────────────────
//  Interfaces
// ─────────────────────────────────────────────────────────────

public interface IFeeScheduleRepository
{
    Task<List<FeeScheduleInfo>> GetAllSchedulesAsync();
    Task<FeeScheduleInfo?> GetScheduleInfoAsync(string feeScheduleId);
    Task<FeeScheduleEntry?> LookupCodeAsync(string feeScheduleId, string procedureCode, string? locality = null);
    Task<List<FeeScheduleEntry>> LookupCodesAsync(string feeScheduleId, IEnumerable<string> procedureCodes, string? locality = null);
    Task<FeeScheduleEntry?> LookupDrgAsync(string feeScheduleId, string drgCode);
    Task UpsertEntryAsync(FeeScheduleEntry entry);
    Task BulkUpsertEntriesAsync(IEnumerable<FeeScheduleEntry> entries);
    Task UpsertScheduleInfoAsync(FeeScheduleInfo info);
}

/// <summary>
/// API keys, addressed by their hash (authentication) or their key id (admin
/// actions). Nothing here takes or stores a key in plaintext.
/// </summary>
public interface IApiKeyRepository
{
    /// <summary>
    /// Run once at startup: hashes keys stored in plaintext before hashing
    /// existed, and creates the indexes. Idempotent.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    Task<ApiKeyRecord?> GetByHashAsync(string keyHash);
    Task<ApiKeyRecord?> GetByIdAsync(string keyId);
    Task IncrementUsageAsync(string keyId, int lineCount);
    Task<ApiKeyRecord> CreateAsync(ApiKeyRecord record);
    Task ResetMonthlyUsageAsync();
    Task<List<ApiKeyRecord>> ListAsync();
    Task DeactivateAsync(string keyId, string deactivatedBy, DateTimeOffset deactivatedAt);
}

public interface IUsageRepository
{
    Task RecordUsageAsync(UsageRecord record);
    Task<List<UsageRecord>> GetUsageAsync(string keyId, DateTimeOffset from, DateTimeOffset to);
}

// ─────────────────────────────────────────────────────────────
//  MongoDB Implementations
// ─────────────────────────────────────────────────────────────

public class MongoFeeScheduleRepository : IFeeScheduleRepository
{
    private readonly IMongoCollection<FeeScheduleEntry> _entries;
    private readonly IMongoCollection<FeeScheduleInfo> _schedules;

    public MongoFeeScheduleRepository(IMongoDatabase database)
    {
        _entries = database.GetCollection<FeeScheduleEntry>("fee_schedule_entries");
        _schedules = database.GetCollection<FeeScheduleInfo>("fee_schedules");

        // Ensure compound index for fast lookups
        var indexBuilder = Builders<FeeScheduleEntry>.IndexKeys;
        _entries.Indexes.CreateMany(
        [
            new CreateIndexModel<FeeScheduleEntry>(
                indexBuilder.Ascending(e => e.FeeScheduleId)
                           .Ascending(e => e.ProcedureCode)
                           .Ascending(e => e.Locality)),
            new CreateIndexModel<FeeScheduleEntry>(
                indexBuilder.Ascending(e => e.FeeScheduleId)
                           .Ascending(e => e.ProcedureCode))
        ]);
    }

    public async Task<List<FeeScheduleInfo>> GetAllSchedulesAsync()
        => await _schedules.Find(_ => true).ToListAsync();

    public async Task<FeeScheduleInfo?> GetScheduleInfoAsync(string feeScheduleId)
        => await _schedules.Find(s => s.Id == feeScheduleId).FirstOrDefaultAsync();

    public async Task<FeeScheduleEntry?> LookupCodeAsync(string feeScheduleId, string procedureCode, string? locality = null)
    {
        var filter = Builders<FeeScheduleEntry>.Filter.And(
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.FeeScheduleId, feeScheduleId),
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.ProcedureCode, procedureCode));

        if (!string.IsNullOrEmpty(locality))
        {
            filter = Builders<FeeScheduleEntry>.Filter.And(filter,
                Builders<FeeScheduleEntry>.Filter.Eq(e => e.Locality, locality));
        }

        return await _entries.Find(filter).FirstOrDefaultAsync();
    }

    public async Task<List<FeeScheduleEntry>> LookupCodesAsync(string feeScheduleId, IEnumerable<string> procedureCodes, string? locality = null)
    {
        var codes = procedureCodes.ToList();
        var filter = Builders<FeeScheduleEntry>.Filter.And(
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.FeeScheduleId, feeScheduleId),
            Builders<FeeScheduleEntry>.Filter.In(e => e.ProcedureCode, codes));

        if (!string.IsNullOrEmpty(locality))
        {
            filter = Builders<FeeScheduleEntry>.Filter.And(filter,
                Builders<FeeScheduleEntry>.Filter.Eq(e => e.Locality, locality));
        }

        return await _entries.Find(filter).ToListAsync();
    }

    public async Task<FeeScheduleEntry?> LookupDrgAsync(string feeScheduleId, string drgCode)
    {
        var filter = Builders<FeeScheduleEntry>.Filter.And(
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.FeeScheduleId, feeScheduleId),
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.ProcedureCode, drgCode));

        return await _entries.Find(filter).FirstOrDefaultAsync();
    }

    public async Task UpsertEntryAsync(FeeScheduleEntry entry)
    {
        var filter = Builders<FeeScheduleEntry>.Filter.And(
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.FeeScheduleId, entry.FeeScheduleId),
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.ProcedureCode, entry.ProcedureCode),
            Builders<FeeScheduleEntry>.Filter.Eq(e => e.Locality, entry.Locality));

        await _entries.ReplaceOneAsync(filter, entry, new ReplaceOptions { IsUpsert = true });
    }

    public async Task BulkUpsertEntriesAsync(IEnumerable<FeeScheduleEntry> entries)
    {
        var operations = entries.Select(entry =>
        {
            var filter = Builders<FeeScheduleEntry>.Filter.And(
                Builders<FeeScheduleEntry>.Filter.Eq(e => e.FeeScheduleId, entry.FeeScheduleId),
                Builders<FeeScheduleEntry>.Filter.Eq(e => e.ProcedureCode, entry.ProcedureCode),
                Builders<FeeScheduleEntry>.Filter.Eq(e => e.Locality, entry.Locality));

            return new ReplaceOneModel<FeeScheduleEntry>(filter, entry) { IsUpsert = true };
        }).ToList();

        if (operations.Count > 0)
            await _entries.BulkWriteAsync(operations);
    }

    public async Task UpsertScheduleInfoAsync(FeeScheduleInfo info)
    {
        var filter = Builders<FeeScheduleInfo>.Filter.Eq(s => s.Id, info.Id);
        await _schedules.ReplaceOneAsync(filter, info, new ReplaceOptions { IsUpsert = true });
    }
}

public class MongoApiKeyRepository : IApiKeyRepository
{
    public const string KeysCollection = "api_keys";
    public const string UsageCollection = "usage_records";

    /// <summary>Field that held the plaintext key before keys were hashed.</summary>
    internal const string LegacyKeyField = "ApiKey";

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<ApiKeyRecord> _collection;
    private readonly ILogger<MongoApiKeyRepository> _logger;

    public MongoApiKeyRepository(IMongoDatabase database, ILogger<MongoApiKeyRepository> logger)
    {
        _database = database;
        _collection = database.GetCollection<ApiKeyRecord>(KeysCollection);
        _logger = logger;
    }

    /// <summary>
    /// Hashes keys stored in plaintext (field <c>ApiKey</c>) before keys were
    /// hashed: sets <c>KeyHash</c>, <c>KeyPrefix</c> and a <c>KeyId</c>, and
    /// removes the plaintext. Usage records that carried the plaintext key get
    /// the key id instead. Each key is updated with a compare-and-set on its
    /// plaintext value, so two replicas starting together cannot double-migrate.
    /// Runs on every start; with nothing left in plaintext it only checks indexes.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var keys = _database.GetCollection<BsonDocument>(KeysCollection);
        var usage = _database.GetCollection<BsonDocument>(UsageCollection);

        // The old unique index on the plaintext field would refuse every
        // migrated record after the first (a missing field indexes as null).
        await DropIndexesOnAsync(keys, LegacyKeyField, ct);
        await DropIndexesOnAsync(usage, LegacyKeyField, ct);

        var legacy = await keys.Find(Builders<BsonDocument>.Filter.Exists(LegacyKeyField)).ToListAsync(ct);
        var migrated = 0;
        var usageMigrated = 0L;
        foreach (var doc in legacy)
        {
            var plaintext = doc[LegacyKeyField];
            if (!plaintext.IsString || string.IsNullOrEmpty(plaintext.AsString))
            {
                // Nothing usable to hash: keep the record, without any key material, inactive.
                await keys.UpdateOneAsync(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]),
                        Builders<BsonDocument>.Filter.Eq(LegacyKeyField, plaintext)),
                    Builders<BsonDocument>.Update.Unset(LegacyKeyField).Set("IsActive", false),
                    cancellationToken: ct);
                continue;
            }

            var key = plaintext.AsString;
            var keyId = doc.TryGetValue("KeyId", out var existingId) && existingId.IsString
                ? existingId.AsString
                : ApiKeyHashing.NewKeyId();

            var result = await keys.UpdateOneAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", doc["_id"]),
                    Builders<BsonDocument>.Filter.Eq(LegacyKeyField, key)),
                Builders<BsonDocument>.Update
                    .Set("KeyId", keyId)
                    .Set("KeyHash", ApiKeyHashing.Hash(key))
                    .Set("KeyPrefix", ApiKeyHashing.Prefix(key))
                    .Unset(LegacyKeyField),
                cancellationToken: ct);
            if (result.ModifiedCount == 0)
                continue; // another replica got there first

            migrated++;
            var usageResult = await usage.UpdateManyAsync(
                Builders<BsonDocument>.Filter.Eq(LegacyKeyField, key),
                Builders<BsonDocument>.Update.Set("KeyId", keyId).Unset(LegacyKeyField),
                cancellationToken: ct);
            usageMigrated += usageResult.ModifiedCount;
        }

        // Usage rows naming a key that no longer exists still must not keep it.
        var orphaned = await usage.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Exists(LegacyKeyField),
            Builders<BsonDocument>.Update.Set("KeyId", "unknown").Unset(LegacyKeyField),
            cancellationToken: ct);

        await _collection.Indexes.CreateManyAsync(new[]
        {
            new CreateIndexModel<ApiKeyRecord>(
                Builders<ApiKeyRecord>.IndexKeys.Ascending(k => k.KeyHash),
                new CreateIndexOptions { Unique = true, Sparse = true, Name = "KeyHash_unique" }),
            new CreateIndexModel<ApiKeyRecord>(
                Builders<ApiKeyRecord>.IndexKeys.Ascending(k => k.KeyId),
                new CreateIndexOptions { Unique = true, Sparse = true, Name = "KeyId_unique" }),
        }, ct);
        await _database.GetCollection<UsageRecord>(UsageCollection).Indexes.CreateOneAsync(
            new CreateIndexModel<UsageRecord>(
                Builders<UsageRecord>.IndexKeys.Ascending(u => u.KeyId).Descending(u => u.Timestamp)),
            cancellationToken: ct);

        if (migrated > 0 || orphaned.ModifiedCount > 0)
        {
            _logger.LogWarning(
                "AUDIT pricing api-key migration: hashed {Keys} plaintext keys, re-keyed {Usage} usage records, cleared {Orphaned} usage records naming unknown keys",
                migrated, usageMigrated, orphaned.ModifiedCount);
        }
    }

    private static async Task DropIndexesOnAsync(IMongoCollection<BsonDocument> collection, string field, CancellationToken ct)
    {
        using var cursor = await collection.Indexes.ListAsync(ct);
        foreach (var index in await cursor.ToListAsync(ct))
        {
            if (index.TryGetValue("key", out var key) && key.AsBsonDocument.Contains(field))
                await collection.Indexes.DropOneAsync(index["name"].AsString, ct);
        }
    }

    public async Task<ApiKeyRecord?> GetByHashAsync(string keyHash)
        => await _collection.Find(k => k.KeyHash == keyHash).FirstOrDefaultAsync();

    public async Task<ApiKeyRecord?> GetByIdAsync(string keyId)
        => await _collection.Find(k => k.KeyId == keyId).FirstOrDefaultAsync();

    public async Task IncrementUsageAsync(string keyId, int lineCount)
    {
        var update = Builders<ApiKeyRecord>.Update.Inc(k => k.CurrentMonthUsage, lineCount);
        await _collection.UpdateOneAsync(k => k.KeyId == keyId, update);
    }

    public async Task<ApiKeyRecord> CreateAsync(ApiKeyRecord record)
    {
        await _collection.InsertOneAsync(record);
        return record;
    }

    public async Task ResetMonthlyUsageAsync()
    {
        var update = Builders<ApiKeyRecord>.Update.Set(k => k.CurrentMonthUsage, 0);
        await _collection.UpdateManyAsync(_ => true, update);
    }

    public async Task<List<ApiKeyRecord>> ListAsync()
        => await _collection.Find(_ => true).ToListAsync();

    public async Task DeactivateAsync(string keyId, string deactivatedBy, DateTimeOffset deactivatedAt)
    {
        var update = Builders<ApiKeyRecord>.Update
            .Set(k => k.IsActive, false)
            .Set(k => k.DeactivatedBy, deactivatedBy)
            .Set(k => k.DeactivatedAt, deactivatedAt);
        await _collection.UpdateOneAsync(k => k.KeyId == keyId, update);
    }
}

public class MongoUsageRepository : IUsageRepository
{
    private readonly IMongoCollection<UsageRecord> _collection;

    // Indexes are created by MongoApiKeyRepository.InitializeAsync, after the
    // plaintext-key migration.
    public MongoUsageRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<UsageRecord>(MongoApiKeyRepository.UsageCollection);
    }

    public async Task RecordUsageAsync(UsageRecord record)
        => await _collection.InsertOneAsync(record);

    public async Task<List<UsageRecord>> GetUsageAsync(string keyId, DateTimeOffset from, DateTimeOffset to)
        => await _collection
            .Find(u => u.KeyId == keyId && u.Timestamp >= from && u.Timestamp <= to)
            .SortByDescending(u => u.Timestamp)
            .ToListAsync();
}
