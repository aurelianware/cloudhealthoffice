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
    /// Get pending submissions ordered by deadline ascending.
    /// Excludes Accepted and permanently Rejected (RetryCount >= 3).
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetPendingSubmissionsAsync(string tenantId, int batchSize = 100);

    /// <summary>
    /// Get encounters approaching their submission deadline (within N days).
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetApproachingDeadlineAsync(int warningDays = 7);

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
    Task ProcessAcknowledgmentAsync(string batchId, string acknowledgmentContent);

    /// <summary>
    /// Flag a submission with DeadlineWarning status.
    /// </summary>
    Task FlagDeadlineWarningAsync(EncounterSubmission submission);
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
