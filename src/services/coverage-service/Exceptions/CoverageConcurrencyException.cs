using CloudHealthOffice.Infrastructure.Data;

namespace CoverageService.Exceptions;

/// <summary>
/// A coverage write based on a stale read: another write changed the coverage
/// (its <see cref="Models.Coverage.Revision"/>) since it was read. Nothing was
/// written; 409 Conflict at the API boundary. The caller re-reads and retries.
/// </summary>
public sealed class CoverageConcurrencyException : ConcurrencyConflictException
{
    public CoverageConcurrencyException(string coverageId, long expectedRevision)
        : base($"Coverage {coverageId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        CoverageId = coverageId;
        ExpectedRevision = expectedRevision;
    }

    public string CoverageId { get; }
    public long ExpectedRevision { get; }
}
