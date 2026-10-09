using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CloudHealthOffice.NcciEngine.Persistence;

/// <summary>
/// MongoDB implementation of INcciRepository.
///
/// Collection layout:
///   ncci_pairs       — NcciEditPair documents, indexed on (tenantId, column1Code, column2Code)
///   mue_entries      — MueEntry documents, indexed on (tenantId, procedureCode)
///   ncci_version     — NcciTableVersion, one document per tenant
///   ncci_load_ledger — NcciLoadRecord, one document per (tenant, quarter, file kind, setting)
///
/// Both lookup methods use a server-side sort + limit 1 to return
/// the most-recent active quarterly entry, consistent with the Cosmos impl.
/// </summary>
internal class NcciRepositoryMongo : INcciRepository
{
    // A CMS practitioner PTP table is ~1M+ rows; one round trip per row
    // would make a quarterly load take hours.
    private const int BulkChunkSize = 1000;

    private readonly IMongoCollection<NcciEditPair> _pairs;
    private readonly IMongoCollection<MueEntry> _mues;
    private readonly IMongoCollection<NcciTableVersion> _version;
    private readonly IMongoCollection<NcciLoadRecord> _ledger;
    private readonly ILogger<NcciRepositoryMongo> _logger;

    public NcciRepositoryMongo(
        IMongoDatabase database,
        IConfiguration configuration,
        ILogger<NcciRepositoryMongo> logger)
    {
        _pairs   = database.GetCollection<NcciEditPair>(
            configuration["NcciEngine:MongoPairCollection"]    ?? "ncci_pairs");
        _mues    = database.GetCollection<MueEntry>(
            configuration["NcciEngine:MongoMueCollection"]     ?? "mue_entries");
        _version = database.GetCollection<NcciTableVersion>(
            configuration["NcciEngine:MongoVersionCollection"] ?? "ncci_version");
        _ledger  = database.GetCollection<NcciLoadRecord>(
            configuration["NcciEngine:MongoLoadLedgerCollection"] ?? "ncci_load_ledger");

        _logger = logger;
    }

    // ── NCCI Edit Pairs ────────────────────────────────────────────

    public async Task<NcciEditPair?> GetEditPairAsync(
        string tenantId, string column1Code, string column2Code,
        DateOnly serviceDate, string? setting = null, CancellationToken ct = default)
    {
        var dos = serviceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var f = Builders<NcciEditPair>.Filter;

        var filter = f.And(
            f.Eq(p => p.TenantId, tenantId),
            f.Eq(p => p.Column1Code, column1Code),
            f.Eq(p => p.Column2Code, column2Code),
            f.Lte(p => p.EffectiveDate, dos),
            f.Or(
                f.Eq(p => p.TerminationDate, null),
                f.Gt(p => p.TerminationDate, dos)));

        if (setting is not null)
            filter &= f.Or(f.Eq(p => p.Setting, null), f.Eq(p => p.Setting, setting));

        return await _pairs
            .Find(filter)
            .SortByDescending(p => p.EffectiveDate)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
    }

    // ── MUE Entries ───────────────────────────────────────────────

    public async Task<MueEntry?> GetMueEntryAsync(
        string tenantId, string procedureCode, DateOnly serviceDate,
        string? setting = null, CancellationToken ct = default)
    {
        var dos = serviceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var f = Builders<MueEntry>.Filter;

        var filter = f.And(
            f.Eq(m => m.TenantId, tenantId),
            f.Eq(m => m.ProcedureCode, procedureCode),
            f.Lte(m => m.EffectiveDate, dos),
            f.Or(
                f.Eq(m => m.TerminationDate, null),
                f.Gt(m => m.TerminationDate, dos)));

        if (setting is not null)
            filter &= f.Or(f.Eq(m => m.Setting, null), f.Eq(m => m.Setting, setting));

        return await _mues
            .Find(filter)
            .SortByDescending(m => m.EffectiveDate)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> ExpireMueEntriesAsync(
        string tenantId, string setting, DateTime quarterStart,
        IReadOnlySet<string> retainedCodes, CancellationToken ct = default)
    {
        var f = Builders<MueEntry>.Filter;
        var filter = f.And(
            f.Eq(m => m.TenantId, tenantId),
            f.Eq(m => m.Setting, setting),
            f.Lt(m => m.EffectiveDate, quarterStart),
            f.Or(f.Eq(m => m.TerminationDate, null), f.Gt(m => m.TerminationDate, quarterStart)),
            f.Nin(m => m.ProcedureCode, retainedCodes));

        var result = await _mues.UpdateManyAsync(
            filter,
            Builders<MueEntry>.Update.Set(m => m.TerminationDate, quarterStart),
            cancellationToken: ct);

        return (int)result.ModifiedCount;
    }

    // ── Quarterly Import ──────────────────────────────────────────

    public async Task<(int PairsWritten, int MueWritten)> UpsertQuarterAsync(
        string tenantId, string quarter,
        IReadOnlyList<NcciEditPair> pairs,
        IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default)
    {
        int pairsWritten = 0;
        int mueWritten = 0;

        foreach (var chunk in pairs.Chunk(BulkChunkSize))
        {
            var writes = chunk.Select(pair => new ReplaceOneModel<NcciEditPair>(
                Builders<NcciEditPair>.Filter.Eq(p => p.Id, pair.Id), pair) { IsUpsert = true });
            await _pairs.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
            pairsWritten += chunk.Length;
        }

        foreach (var chunk in entries.Chunk(BulkChunkSize))
        {
            var writes = chunk.Select(entry => new ReplaceOneModel<MueEntry>(
                Builders<MueEntry>.Filter.Eq(m => m.Id, entry.Id), entry) { IsUpsert = true });
            await _mues.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false }, ct);
            mueWritten += chunk.Length;
        }

        _logger.LogInformation(
            "Mongo NCCI import for quarter {Quarter}: {Pairs} pairs, {Mue} MUE entries upserted",
            quarter, pairsWritten, mueWritten);

        return (pairsWritten, mueWritten);
    }

    // ── CMS Load Ledger ───────────────────────────────────────────

    public async Task<NcciLoadRecord?> GetLoadRecordAsync(string tenantId, string id, CancellationToken ct = default)
    {
        var f = Builders<NcciLoadRecord>.Filter;
        return await _ledger
            .Find(f.And(f.Eq(r => r.Id, id), f.Eq(r => r.TenantId, tenantId)))
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveLoadRecordAsync(NcciLoadRecord record, CancellationToken ct = default)
    {
        await _ledger.ReplaceOneAsync(
            Builders<NcciLoadRecord>.Filter.Eq(r => r.Id, record.Id),
            record,
            new ReplaceOptions { IsUpsert = true },
            ct);
    }

    public async Task<IReadOnlyList<NcciLoadRecord>> ListLoadRecordsAsync(
        string tenantId, string quarter, CancellationToken ct = default)
    {
        var f = Builders<NcciLoadRecord>.Filter;
        return await _ledger
            .Find(f.And(f.Eq(r => r.TenantId, tenantId), f.Eq(r => r.Quarter, quarter)))
            .ToListAsync(ct);
    }

    // ── Version Metadata ──────────────────────────────────────────

    public async Task<NcciTableVersion?> GetCurrentVersionAsync(string tenantId, CancellationToken ct = default)
    {
        return await _version
            .Find(Builders<NcciTableVersion>.Filter.Eq(v => v.TenantId, tenantId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveVersionAsync(NcciTableVersion version, CancellationToken ct = default)
    {
        var filter = Builders<NcciTableVersion>.Filter.Eq(v => v.TenantId, version.TenantId);
        await _version.ReplaceOneAsync(filter, version, new ReplaceOptions { IsUpsert = true }, ct);
    }
}
