namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Enrollment Operations
// ---------------------------------------------------------------------------

public interface IEnrollmentOperationsService
{
    Task<EnrollmentDailySummary> GetTodaySummaryAsync();
    Task<List<EnrollmentFile>> GetRecentFilesAsync(int days = 7);
    Task<EnrollmentFileDetail> GetFileDetailAsync(string fileId);
}
