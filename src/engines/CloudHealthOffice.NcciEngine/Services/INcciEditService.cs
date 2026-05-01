using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Models;

namespace CloudHealthOffice.NcciEngine.Services;

/// <summary>
/// Core NCCI/MUE editing service.
///
/// Consumers (claims-service NcciEditsStage in capability 5.7; future
/// state-Medicaid EDI ingest) call <see cref="ScrubAsync"/> once per
/// claim to detect bundling and unit-limit violations before payment.
/// </summary>
public interface INcciEditService
{
    /// <summary>
    /// Apply all NCCI Column 1/Column 2 edits and MUE unit checks to
    /// the supplied claim.  Returns a <see cref="NcciScrubResult"/> whose
    /// <see cref="NcciScrubResult.Passed"/> property indicates whether
    /// the claim is clean.
    /// </summary>
    Task<NcciScrubResult> ScrubAsync(NcciScrubRequest request, CancellationToken ct = default);

    /// <summary>
    /// Returns metadata about the NCCI/MUE table currently active for
    /// the tenant — useful for the portal admin view and audit logs.
    /// </summary>
    Task<NcciTableVersion?> GetTableVersionAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Import a fresh set of NCCI edit pairs and MUE entries for a CMS
    /// quarterly release.  Existing records for the same effective quarter
    /// are replaced.  Returns counts of records written.
    /// </summary>
    Task<(int NcciPairsWritten, int MueEntriesWritten)> ImportQuarterlyUpdateAsync(
        string tenantId,
        string quarter,
        IReadOnlyList<NcciEditPair> pairs,
        IReadOnlyList<MueEntry> entries,
        CancellationToken ct = default);
}
