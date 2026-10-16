using CloudHealthOffice.Infrastructure.Data;

namespace RiskAdjustmentService.Repositories;

/// <summary>
/// A member risk score write based on a stale read: another write changed the
/// score (its <see cref="Models.MemberRiskScore.Revision"/>) since it was read.
/// Nothing was written; 409 Conflict at the API boundary. The caller re-reads
/// and retries.
/// </summary>
public sealed class RiskScoreConcurrencyException : ConcurrencyConflictException
{
    public RiskScoreConcurrencyException(string scoreId, long expectedRevision)
        : base($"Risk score {scoreId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
    {
        ScoreId = scoreId;
        ExpectedRevision = expectedRevision;
    }

    public string ScoreId { get; }
    public long ExpectedRevision { get; }
}
