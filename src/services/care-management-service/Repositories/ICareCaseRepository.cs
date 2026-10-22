using CareManagementService.Models;

namespace CareManagementService.Repositories;

/// <summary>A stale write: the case changed since it was read.</summary>
public sealed class CareCaseConcurrencyException(string caseId, long expectedRevision)
    : InvalidOperationException(
        $"Care case {caseId} was changed by another write (expected revision {expectedRevision}); re-read it and retry.")
{
    public string CaseId { get; } = caseId;
    public long ExpectedRevision { get; } = expectedRevision;
}

/// <summary>Two cases in the same tenant cannot share a case number.</summary>
public sealed class DuplicateCaseNumberException(string caseNumber)
    : InvalidOperationException($"Care case number {caseNumber} already exists in this tenant.")
{
    public string CaseNumber { get; } = caseNumber;
}

/// <summary>Care cases, scoped to the caller's tenant on every read and write.</summary>
public interface ICareCaseRepository
{
    Task<CareCase?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<CareCase?> GetByCaseNumberAsync(string caseNumber, CancellationToken ct = default);

    /// <summary>A member's cases, newest referral first.</summary>
    Task<IReadOnlyList<CareCase>> ListByMemberAsync(string memberId, CancellationToken ct = default);

    /// <summary>
    /// A care manager's open caseload, or every open case when no care
    /// manager is named. Closed and declined cases are left out.
    /// </summary>
    Task<IReadOnlyList<CareCase>> ListOpenAsync(
        string? careManagerId = null, CareProgram? program = null, int limit = 200, CancellationToken ct = default);

    /// <summary>
    /// Open cases whose next contact date has passed, which is the care
    /// manager's own overdue list.
    /// </summary>
    Task<IReadOnlyList<CareCase>> ListContactOverdueAsync(DateTime asOfUtc, CancellationToken ct = default);

    Task<CareCase> CreateAsync(CareCase careCase, CancellationToken ct = default);

    /// <summary>
    /// Replaces the case, but only while its stored revision is the one that
    /// was read; otherwise <see cref="CareCaseConcurrencyException"/>.
    /// </summary>
    Task<CareCase> UpdateAsync(CareCase careCase, CancellationToken ct = default);
}
