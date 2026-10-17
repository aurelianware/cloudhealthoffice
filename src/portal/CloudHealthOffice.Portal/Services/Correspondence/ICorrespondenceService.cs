namespace CloudHealthOffice.Portal.Services;

// ---------------------------------------------------------------------------
// Correspondence
// ---------------------------------------------------------------------------

public interface ICorrespondenceService
{
    Task<CorrespondenceSummary> GetSummaryAsync();
    Task<List<CorrespondenceItem>> GetQueueAsync(string? type = null,
        string? status = null, int limit = 50);
    Task<List<RfaiTrackingItem>> GetOutstandingRfaisAsync();
}
