using EncounterSubmissionService.Models;

namespace EncounterSubmissionService.Services;

/// <summary>
/// Core business logic for managing the 60-day FMMIS encounter submission
/// window: creating tracking records, batching claims, processing 999
/// acknowledgments, and firing deadline warning events.
/// </summary>
public interface IEncounterSubmissionService
{
    /// <summary>
    /// Create a new encounter submission record for an adjudicated claim.
    /// Looks up TenantComplianceConfig to determine EncounterSubmissionDays.
    /// </summary>
    Task<EncounterSubmission> CreateSubmissionRecordAsync(string claimId, string tenantId, DateTime adjudicatedAt);

    /// <summary>
    /// Get an encounter submission by ID.
    /// </summary>
    Task<EncounterSubmission?> GetByIdAsync(string id, string tenantId);

    /// <summary>
    /// Get pending submissions ordered by deadline ascending, paginated.
    /// Excludes Accepted and permanently Rejected (RetryCount >= 3).
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetPendingSubmissionsAsync(
        string tenantId, int page = 1, int pageSize = 50);

    /// <summary>
    /// Get encounters approaching their submission deadline (within N days) for a tenant.
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetDeadlineWarningsAsync(string tenantId, int warningDays = 7);

    /// <summary>
    /// Get encounters approaching their submission deadline across all tenants.
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetApproachingDeadlineAsync(int warningDays = 7);

    /// <summary>
    /// Get status counts for a tenant (pending, batched, submitted, accepted, warning, rejected).
    /// </summary>
    Task<EncounterStatusSummary> GetStatusSummaryAsync(string tenantId);

    /// <summary>
    /// Fetch claims from claims-service, transform via FMMIS pipeline,
    /// assemble a batch file, and update submission statuses to Batched.
    /// </summary>
    Task<FmmisSubmissionFileDto> BuildFmmisSubmissionBatchAsync(
        IEnumerable<EncounterSubmission> submissions, string tenantId);

    /// <summary>
    /// Process a 999 acknowledgment response for a batch.
    /// Updates submission statuses and populates error details on rejections.
    /// </summary>
    Task ProcessAcknowledgmentAsync(string batchId, string acknowledgmentContent, string tenantId);

    /// <summary>
    /// Flag a submission with DeadlineWarning status.
    /// </summary>
    Task FlagDeadlineWarningAsync(EncounterSubmission submission);

    /// <summary>
    /// Manually retry a rejected submission: reset status to Pending
    /// so it is included in the next batch cycle.
    /// </summary>
    Task<EncounterSubmission> RetrySubmissionAsync(string submissionId, string tenantId);
}

/// <summary>
/// Dashboard summary of encounter submission counts by status.
/// </summary>
public class EncounterStatusSummary
{
    public string TenantId { get; set; } = string.Empty;
    public int Pending { get; set; }
    public int Batched { get; set; }
    public int Submitted { get; set; }
    public int Accepted { get; set; }
    public int PartialAccept { get; set; }
    public int Rejected { get; set; }
    public int DeadlineWarning { get; set; }
    public int Total { get; set; }
}

/// <summary>
/// DTO mirroring the FMMIS submission file produced by claims-service.
/// </summary>
public class FmmisSubmissionFileDto
{
    public string FileName { get; set; } = string.Empty;
    public byte[] Content { get; set; } = Array.Empty<byte>();
    public int TransactionCount { get; set; }
    public List<string> ClaimIds { get; set; } = new();
    public string BatchId { get; set; } = string.Empty;
}
