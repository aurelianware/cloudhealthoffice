namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Admin-console read path for raw EDI import outcomes — 834 enrollment
/// batches (enrollment-import-service) and 837 claim imports
/// (claims-service). Distinct from <see cref="IMemberService.GetMember834TransactionsAsync"/>,
/// which is member-scoped and proxied through member-service; this is a
/// tenant-wide admin view, so it calls both owning services directly, same
/// as <see cref="IClaimsService.GetMassAdjudicationRunsAsync"/> does for
/// claims-service.
/// </summary>
public interface IEdiTransactionsService
{
    Task<List<Enrollment834Record>> GetEnrollment834TransactionsAsync(int limit = 100);
    Task<List<ClaimImportTransactionRecord>> GetClaimImportTransactionsAsync(int limit = 100);

    /// <summary>Batch-level 834 run summaries — one row per import, not per member. See <see cref="EnrollmentImportRunRecord"/>.</summary>
    Task<List<EnrollmentImportRunRecord>> GetEnrollmentImportRunsAsync(int limit = 100);
}
