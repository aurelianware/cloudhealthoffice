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
    /// <para>Setting filter: when <paramref name="setting"/> is null, rows of
    /// every setting match (legacy behavior). Otherwise rows for that setting
    /// match, plus setting-less (seed / legacy) rows when
    /// <paramref name="includeUnscoped"/> is true.</para>
    /// </summary>
    Task<NcciEditPair?> GetEditPairAsync(
        string tenantId,
        string column1Code,
        string column2Code,
        DateOnly serviceDate,
        string? setting = null,
        bool includeUnscoped = true,
        CancellationToken ct = default);

    // ── MUE Entries ───────────────────────────────────────────────

    /// <summary>
    /// Look up the active MUE entry for a procedure code on a given date.
    /// Returns null if no active MUE exists for the code.
    /// The setting filter works as for <see cref="GetEditPairAsync"/>.
    /// </summary>
    Task<MueEntry?> GetMueEntryAsync(
        string tenantId,
        string procedureCode,
        DateOnly serviceDate,
        string? setting = null,
        bool includeUnscoped = true,
        CancellationToken ct = default);

    // ── CMS snapshot reconciliation ───────────────────────────────

    /// <summary>
    /// After a full MUE table for <paramref name="setting"/> effective
    /// <paramref name="quarterStart"/> was upserted: delete rows of that same
    /// quarter whose code is not in <paramref name="retainedCodes"/> (a
    /// corrected re-publication dropped them), and end at
    /// <paramref name="quarterStart"/> every earlier row still active past it
    /// whose code the new table no longer lists.
    /// </summary>
    Task<(int Expired, int Deleted)> ReconcileMueSnapshotAsync(
        string tenantId,
        string setting,
        DateTime quarterStart,
        IReadOnlySet<string> retainedCodes,
        CancellationToken ct = default);

    /// <summary>
    /// After PTP load <paramref name="loadId"/> wrote file slot
    /// <paramref name="sourceKey"/> for <paramref name="quarter"/>: rows from
    /// that slot the load did not rewrite are deleted when they came from the
    /// same quarter (a correction), or ended at <paramref name="quarterStart"/>
    /// when they came from an earlier quarter.
    /// </summary>
    Task<(int Expired, int Deleted)> ReconcilePtpSnapshotAsync(
        string tenantId,
        string sourceKey,
        string quarter,
        DateTime quarterStart,
        string loadId,
        CancellationToken ct = default);

    // ── CMS Load Ledger ───────────────────────────────────────────

    /// <summary>Read one CMS load ledger row by <see cref="NcciLoadRecord.Id"/>.</summary>
    Task<NcciLoadRecord?> GetLoadRecordAsync(string tenantId, string id, CancellationToken ct = default);

    /// <summary>Insert or replace a CMS load ledger row.</summary>
    Task SaveLoadRecordAsync(NcciLoadRecord record, CancellationToken ct = default);

    /// <summary>CMS load ledger rows for one quarter, or for every quarter when <paramref name="quarter"/> is null.</summary>
    Task<IReadOnlyList<NcciLoadRecord>> ListLoadRecordsAsync(string tenantId, string? quarter, CancellationToken ct = default);

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
