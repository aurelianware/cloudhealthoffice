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
///   ncci_pairs    — NcciEditPair documents, indexed on (tenantId, column1Code, column2Code)
///   mue_entries   — MueEntry documents, indexed on (tenantId, procedureCode)
///   ncci_version  — NcciTableVersion, one document per tenant
///
/// Both lookup methods use a server-side sort + limit 1 to return
/// the most-recent active quarterly entry, consistent with the Cosmos impl.
/// </summary>
internal class NcciRepositoryMongo : INcciRepository
{
    private readonly IMongoCollection<NcciEditPair> _pairs;
    private readonly IMongoCollection<MueEntry> _mues;
    private readonly IMongoCollection<NcciTableVersion> _version;
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

        _logger = logger;
    }

    // ── NCCI Edit Pairs ────────────────────────────────────────────

    public async Task<NcciEditPair?> GetEditPairAsync(
        string tenantId, string column1Code, string column2Code,
        DateOnly serviceDate, CancellationToken ct = default)
    {
        var dos = serviceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var filter = Builders<NcciEditPair>.Filter.And(
            Builders<NcciEditPair>.Filter.Eq(p => p.TenantId, tenantId),
            Builders<NcciEditPair>.Filter.Eq(p => p.Column1Code, column1Code),
            Builders<NcciEditPair>.Filter.Eq(p => p.Column2Code, column2Code),
            Builders<NcciEditPair>.Filter.Lte(p => p.EffectiveDate, dos),
            Builders<NcciEditPair>.Filter.Or(
                Builders<NcciEditPair>.Filter.Eq(p => p.TerminationDate, null),
                Builders<NcciEditPair>.Filter.Gt(p => p.TerminationDate, dos)));

        return await _pairs
            .Find(filter)
            .SortByDescending(p => p.EffectiveDate)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
    }

    // ── MUE Entries ───────────────────────────────────────────────

    public async Task<MueEntry?> GetMueEntryAsync(
        string tenantId, string procedureCode, DateOnly serviceDate, CancellationToken ct = default)
    {
        var dos = serviceDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var filter = Builders<MueEntry>.Filter.And(
            Builders<MueEntry>.Filter.Eq(m => m.TenantId, tenantId),
            Builders<MueEntry>.Filter.Eq(m => m.ProcedureCode, procedureCode),
            Builders<MueEntry>.Filter.Lte(m => m.EffectiveDate, dos),
            Builders<MueEntry>.Filter.Or(
                Builders<MueEntry>.Filter.Eq(m => m.TerminationDate, null),
                Builders<MueEntry>.Filter.Gt(m => m.TerminationDate, dos)));

        return await _mues
            .Find(filter)
            .SortByDescending(m => m.EffectiveDate)
            .Limit(1)
            .FirstOrDefaultAsync(ct);
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

        foreach (var pair in pairs)
        {
            var filter = Builders<NcciEditPair>.Filter.Eq(p => p.Id, pair.Id);
            await _pairs.ReplaceOneAsync(filter, pair,
                new ReplaceOptions { IsUpsert = true }, ct);
            pairsWritten++;
        }

        foreach (var entry in entries)
        {
            var filter = Builders<MueEntry>.Filter.Eq(m => m.Id, entry.Id);
            await _mues.ReplaceOneAsync(filter, entry,
                new ReplaceOptions { IsUpsert = true }, ct);
            mueWritten++;
        }

        _logger.LogInformation(
            "Mongo NCCI import for quarter {Quarter}: {Pairs} pairs, {Mue} MUE entries upserted",
            quarter, pairsWritten, mueWritten);

        return (pairsWritten, mueWritten);
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
