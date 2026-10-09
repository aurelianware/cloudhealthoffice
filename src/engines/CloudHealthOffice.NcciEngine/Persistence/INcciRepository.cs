using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;

namespace CloudHealthOffice.NcciEngine.Persistence;

/// <summary>
/// Persistence abstraction for NCCI edit pairs, MUE entries, and table version metadata.
/// Implemented for Cosmos DB and MongoDB.
/// </summary>
public interface INcciRepository
{
    // ── NCCI Edit Pairs ────────────────────────────────────────────

    /// <summary>
    /// Look up an active NCCI Column 1 / Column 2 edit pair for the
    /// supplied procedure codes and date of service.
    /// Returns null if no active pair exists.
    /// When <paramref name="setting"/> is given (see <see cref="NcciSettings"/>),
    /// only rows for that setting or rows with no setting match; when null,
    /// rows of every setting match.
    /// </summary>
    Task<NcciEditPair?> GetEditPairAsync(
        string tenantId,
        string column1Code,
        string column2Code,
        DateOnly serviceDate,
        string? setting = null,
        CancellationToken ct = default);

    // ── MUE Entries ───────────────────────────────────────────────

    /// <summary>
    /// Look up the active MUE entry for a procedure code on a given date.
    /// Returns null if no active MUE exists for the code.
    /// <paramref name="setting"/> filters as for <see cref="GetEditPairAsync"/>.
    /// </summary>
    Task<MueEntry?> GetMueEntryAsync(
        string tenantId,
        string procedureCode,
        DateOnly serviceDate,
        string? setting = null,
        CancellationToken ct = default);

    /// <summary>
    /// Terminate (TerminationDate = <paramref name="quarterStart"/>) every
    /// still-active MUE row for <paramref name="setting"/> that took effect
    /// before <paramref name="quarterStart"/> and whose code is not in
    /// <paramref name="retainedCodes"/> — i.e. codes a new quarterly MUE
    /// table no longer lists. Returns the number of rows terminated.
    /// </summary>
    Task<int> ExpireMueEntriesAsync(
        string tenantId,
        string setting,
        DateTime quarterStart,
        IReadOnlySet<string> retainedCodes,
        CancellationToken ct = default);

    // ── CMS Load Ledger ───────────────────────────────────────────

    /// <summary>Read one CMS load ledger row by <see cref="NcciLoadRecord.Id"/>.</summary>
    Task<NcciLoadRecord?> GetLoadRecordAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Insert or replace a CMS load ledger row.</summary>
    Task SaveLoadRecordAsync(NcciLoadRecord record, CancellationToken ct = default);

    /// <summary>Every CMS load ledger row for one quarter.</summary>
    Task<IReadOnlyList<NcciLoadRecord>> ListLoadRecordsAsync(string tenantId, string quarter, CancellationToken ct = default);

    // ── Quarterly Import ──────────────────────────────────────────

    /// <summary>
    /// Upsert all NCCI pairs and MUE entries for a quarterly CMS release.
    /// Existing documents for the same effective quarter are replaced.
    /// </summary>
    Task<(int PairsWritten, int MueWritten)> UpsertQuarterAsync(
        string tenantId,
        string quarter,
        IReadOnlyList<NcciEditPair> pairs,
        IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default);

    // ── Version Metadata ──────────────────────────────────────────

    /// <summary>
    /// Get the version metadata for the currently active NCCI/MUE tables.
    /// </summary>
    Task<NcciTableVersion?> GetCurrentVersionAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Persist version metadata after a quarterly import completes.
    /// </summary>
    Task SaveVersionAsync(NcciTableVersion version, CancellationToken ct = default);
}
