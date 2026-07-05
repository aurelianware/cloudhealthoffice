using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Persistence;

namespace CloudHealthOffice.NcciEngine.Tests;

/// <summary>
/// In-memory test double for INcciRepository.
/// Seed pairs via AddEditPair / AddMueEntry before calling ScrubAsync.
/// </summary>
internal sealed class FakeNcciRepository : INcciRepository
{
    private readonly List<NcciEditPair> _pairs = new();
    private readonly List<MueEntry> _mues = new();

    public int EditPairLookupCount { get; private set; }
    public int MueLookupCount { get; private set; }

    public void AddEditPair(NcciEditPair pair) => _pairs.Add(pair);

    public void AddMueEntry(MueEntry mue) => _mues.Add(mue);

    public Task<NcciEditPair?> GetEditPairAsync(
        string tenantId, string column1Code, string column2Code,
        DateOnly serviceDate, CancellationToken ct = default)
    {
        EditPairLookupCount++;

        var match = _pairs.FirstOrDefault(p =>
            p.TenantId == tenantId &&
            p.Column1Code == column1Code &&
            p.Column2Code == column2Code &&
            p.EffectiveDate.Date <= serviceDate.ToDateTime(TimeOnly.MinValue).Date &&
            (p.TerminationDate == null || p.TerminationDate.Value.Date >= serviceDate.ToDateTime(TimeOnly.MinValue).Date));

        return Task.FromResult(match);
    }

    public Task<MueEntry?> GetMueEntryAsync(
        string tenantId, string procedureCode,
        DateOnly serviceDate, CancellationToken ct = default)
    {
        MueLookupCount++;

        var match = _mues.FirstOrDefault(m =>
            m.TenantId == tenantId &&
            m.ProcedureCode == procedureCode &&
            m.EffectiveDate.Date <= serviceDate.ToDateTime(TimeOnly.MinValue).Date &&
            (m.TerminationDate == null || m.TerminationDate.Value.Date >= serviceDate.ToDateTime(TimeOnly.MinValue).Date));

        return Task.FromResult(match);
    }

    public Task<(int PairsWritten, int MueWritten)> UpsertQuarterAsync(
        string tenantId, string quarter,
        IReadOnlyList<NcciEditPair> pairs, IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default)
    {
        _pairs.AddRange(pairs);
        _mues.AddRange(entries);
        return Task.FromResult((pairs.Count, entries.Count));
    }

    public Task<NcciTableVersion?> GetCurrentVersionAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult<NcciTableVersion?>(null);

    public Task SaveVersionAsync(NcciTableVersion version, CancellationToken ct = default)
        => Task.CompletedTask;
}
