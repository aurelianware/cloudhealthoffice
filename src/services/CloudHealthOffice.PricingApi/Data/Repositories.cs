using CloudHealthOffice.PricingApi.Models;
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

public interface IApiKeyRepository
{
    Task<ApiKeyRecord?> GetByKeyAsync(string apiKey);
    Task IncrementUsageAsync(string apiKey, int lineCount);
    Task<ApiKeyRecord> CreateAsync(ApiKeyRecord record);
    Task ResetMonthlyUsageAsync();
}

public interface IUsageRepository
{
    Task RecordUsageAsync(UsageRecord record);
    Task<List<UsageRecord>> GetUsageAsync(string apiKey, DateTimeOffset from, DateTimeOffset to);
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
    private readonly IMongoCollection<ApiKeyRecord> _collection;

    public MongoApiKeyRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<ApiKeyRecord>("api_keys");
        _collection.Indexes.CreateOne(
            new CreateIndexModel<ApiKeyRecord>(
                Builders<ApiKeyRecord>.IndexKeys.Ascending(k => k.ApiKey),
                new CreateIndexOptions { Unique = true }));
    }

    public async Task<ApiKeyRecord?> GetByKeyAsync(string apiKey)
        => await _collection.Find(k => k.ApiKey == apiKey).FirstOrDefaultAsync();

    public async Task IncrementUsageAsync(string apiKey, int lineCount)
    {
        var update = Builders<ApiKeyRecord>.Update.Inc(k => k.CurrentMonthUsage, lineCount);
        await _collection.UpdateOneAsync(k => k.ApiKey == apiKey, update);
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
}

public class MongoUsageRepository : IUsageRepository
{
    private readonly IMongoCollection<UsageRecord> _collection;

    public MongoUsageRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<UsageRecord>("usage_records");
        _collection.Indexes.CreateOne(
            new CreateIndexModel<UsageRecord>(
                Builders<UsageRecord>.IndexKeys
                    .Ascending(u => u.ApiKey)
                    .Descending(u => u.Timestamp)));
    }

    public async Task RecordUsageAsync(UsageRecord record)
        => await _collection.InsertOneAsync(record);

    public async Task<List<UsageRecord>> GetUsageAsync(string apiKey, DateTimeOffset from, DateTimeOffset to)
        => await _collection
            .Find(u => u.ApiKey == apiKey && u.Timestamp >= from && u.Timestamp <= to)
            .SortByDescending(u => u.Timestamp)
            .ToListAsync();
}
