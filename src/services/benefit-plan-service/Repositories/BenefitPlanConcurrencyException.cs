using CloudHealthOffice.Infrastructure.Data;

namespace BenefitPlanService.Repositories;

/// <summary>
/// A benefit-plan version write based on a stale read: another write changed
/// the version row (its <see cref="Models.BenefitPlan.Revision"/> or its
/// <see cref="Models.BenefitPlan.VersionState"/>) since it was read, for
/// example a concurrent publish of the same draft or a concurrent supersede of
/// the same Published version. Nothing was written; 409 Conflict at the API
/// boundary. The caller re-reads and retries.
/// </summary>
public sealed class BenefitPlanConcurrencyException : ConcurrencyConflictException
{
    public BenefitPlanConcurrencyException(string id, long expectedRevision, Exception? inner = null)
        : base($"Benefit plan version {id} was changed by another write (expected revision {expectedRevision}); re-read it and retry.", inner)
    {
        BenefitPlanDocumentId = id;
        ExpectedRevision = expectedRevision;
    }

    /// <summary>The version row's document id (<see cref="Models.BenefitPlan.Id"/>).</summary>
    public string BenefitPlanDocumentId { get; }
    public long ExpectedRevision { get; }
}
