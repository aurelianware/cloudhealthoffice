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
    /// </summary>
    Task<NcciEditPair?> GetEditPairAsync(
        string tenantId,
        string column1Code,
        string column2Code,
        DateOnly serviceDate,
        CancellationToken ct = default);

    // ── MUE Entries ───────────────────────────────────────────────

    /// <summary>
    /// Look up the active MUE entry for a procedure code on a given date.
    /// Returns null if no active MUE exists for the code.
    /// </summary>
    Task<MueEntry?> GetMueEntryAsync(
        string tenantId,
        string procedureCode,
        DateOnly serviceDate,
        CancellationToken ct = default);

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
