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
    /// Called by the <see cref="KafkaConsumers.AdjudicationCompletedConsumer"/>.
    /// </summary>
    Task<EncounterSubmission> CreateAsync(string tenantId, string claimId, DateTime adjudicatedAt);

    /// <summary>
    /// Get an encounter submission by ID.
    /// </summary>
    Task<EncounterSubmission?> GetByIdAsync(string id);

    /// <summary>
    /// Get all pending encounters for a tenant that are ready to be batched.
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetPendingByTenantAsync(string tenantId);

    /// <summary>
    /// Get encounters approaching their submission deadline (within N days).
    /// </summary>
    Task<IEnumerable<EncounterSubmission>> GetApproachingDeadlineAsync(int warningDays = 7);

    /// <summary>
    /// Mark encounters as batched (included in an FMMIS file).
    /// </summary>
    Task BatchAsync(IEnumerable<string> submissionIds, string batchId);

    /// <summary>
    /// Mark a batch as submitted (transmitted to FMMIS).
    /// </summary>
    Task MarkSubmittedAsync(string batchId, DateTime submittedAt);

    /// <summary>
    /// Process a 999 acknowledgment for a batch.
    /// </summary>
    Task ProcessAcknowledgmentAsync(string batchId, string acknowledgmentCode, List<string>? errors = null);
}
