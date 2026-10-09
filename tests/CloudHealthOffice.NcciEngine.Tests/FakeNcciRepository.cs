using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Persistence;

namespace CloudHealthOffice.NcciEngine.Tests;

/// <summary>
/// In-memory test double for INcciRepository.
/// Seed pairs via AddEditPair / AddMueEntry before calling ScrubAsync.
/// Upserts replace by document id, like the Mongo and Cosmos stores.
/// </summary>
internal sealed class FakeNcciRepository : INcciRepository
{
    private readonly List<NcciEditPair> _pairs = new();
    private readonly List<MueEntry> _mues = new();
    private readonly Dictionary<string, NcciLoadRecord> _ledger = new();

    public int EditPairLookupCount { get; private set; }
    public int MueLookupCount { get; private set; }
    public int PairUpsertCount { get; private set; }
    public int MueUpsertCount { get; private set; }
    public NcciTableVersion? Version { get; private set; }

    public IReadOnlyList<NcciEditPair> Pairs => _pairs;
    public IReadOnlyList<MueEntry> Mues => _mues;

    public void AddEditPair(NcciEditPair pair) => _pairs.Add(pair);

    public void AddMueEntry(MueEntry mue) => _mues.Add(mue);

    public Task<NcciEditPair?> GetEditPairAsync(
        string tenantId, string column1Code, string column2Code,
        DateOnly serviceDate, string? setting = null, CancellationToken ct = default)
    {
        EditPairLookupCount++;
        var dos = serviceDate.ToDateTime(TimeOnly.MinValue).Date;

        var match = _pairs
            .Where(p =>
                p.TenantId == tenantId &&
                p.Column1Code == column1Code &&
                p.Column2Code == column2Code &&
                (setting is null || p.Setting is null || p.Setting == setting) &&
                p.EffectiveDate.Date <= dos &&
                (p.TerminationDate == null || p.TerminationDate.Value.Date > dos))
            .OrderByDescending(p => p.EffectiveDate)
            .FirstOrDefault();

        return Task.FromResult(match);
    }

    public Task<MueEntry?> GetMueEntryAsync(
        string tenantId, string procedureCode,
        DateOnly serviceDate, string? setting = null, CancellationToken ct = default)
    {
        MueLookupCount++;
        var dos = serviceDate.ToDateTime(TimeOnly.MinValue).Date;

        var match = _mues
            .Where(m =>
                m.TenantId == tenantId &&
                m.ProcedureCode == procedureCode &&
                (setting is null || m.Setting is null || m.Setting == setting) &&
                m.EffectiveDate.Date <= dos &&
                (m.TerminationDate == null || m.TerminationDate.Value.Date > dos))
            .OrderByDescending(m => m.EffectiveDate)
            .FirstOrDefault();

        return Task.FromResult(match);
    }

    public Task<int> ExpireMueEntriesAsync(
        string tenantId, string setting, DateTime quarterStart,
        IReadOnlySet<string> retainedCodes, CancellationToken ct = default)
    {
        var expired = 0;
        foreach (var m in _mues.Where(m =>
                     m.TenantId == tenantId &&
                     m.Setting == setting &&
                     m.EffectiveDate < quarterStart &&
                     (m.TerminationDate == null || m.TerminationDate > quarterStart) &&
                     !retainedCodes.Contains(m.ProcedureCode)))
        {
            m.TerminationDate = quarterStart;
            expired++;
        }
        return Task.FromResult(expired);
    }

    public Task<(int PairsWritten, int MueWritten)> UpsertQuarterAsync(
        string tenantId, string quarter,
        IReadOnlyList<NcciEditPair> pairs, IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default)
    {
        foreach (var pair in pairs)
        {
            _pairs.RemoveAll(p => p.Id.Length > 0 && p.Id == pair.Id);
            _pairs.Add(pair);
            PairUpsertCount++;
        }
        foreach (var entry in entries)
        {
            _mues.RemoveAll(m => m.Id.Length > 0 && m.Id == entry.Id);
            _mues.Add(entry);
            MueUpsertCount++;
        }
        return Task.FromResult((pairs.Count, entries.Count));
    }

    public Task<NcciLoadRecord?> GetLoadRecordAsync(string tenantId, string id, CancellationToken ct = default)
        => Task.FromResult(_ledger.TryGetValue(id, out var r) && r.TenantId == tenantId ? r : null);

    public Task SaveLoadRecordAsync(NcciLoadRecord record, CancellationToken ct = default)
    {
        _ledger[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NcciLoadRecord>> ListLoadRecordsAsync(string tenantId, string quarter, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NcciLoadRecord>>(
            _ledger.Values.Where(r => r.TenantId == tenantId && r.Quarter == quarter).ToList());

    public Task<NcciTableVersion?> GetCurrentVersionAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult(Version is not null && Version.TenantId == tenantId ? Version : null);

    public Task SaveVersionAsync(NcciTableVersion version, CancellationToken ct = default)
    {
        Version = version;
        return Task.CompletedTask;
    }
}
