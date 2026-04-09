using EncounterSubmissionService.Models;

namespace EncounterSubmissionService.Services;

/// <summary>
/// Manages the lifecycle of FMMIS encounter submissions: tracking records,
/// batching, acknowledgment processing, and deadline monitoring.
/// </summary>
public class EncounterSubmissionServiceImpl : IEncounterSubmissionService
{
    private readonly ILogger<EncounterSubmissionServiceImpl> _logger;

    /// <summary>
    /// AHCA MCO contract: encounters must be submitted within 60 days of adjudication.
    /// </summary>
    private const int SubmissionWindowDays = 60;

    public EncounterSubmissionServiceImpl(ILogger<EncounterSubmissionServiceImpl> logger)
    {
        _logger = logger;
    }

    public Task<EncounterSubmission> CreateAsync(string tenantId, string claimId, DateTime adjudicatedAt)
    {
        var submission = new EncounterSubmission
        {
            TenantId = tenantId,
            ClaimId = claimId,
            ClaimAdjudicatedAt = adjudicatedAt,
            StateCode = "FL",
            SubmissionDeadline = adjudicatedAt.AddDays(SubmissionWindowDays),
            Status = EncounterSubmissionStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _logger.LogInformation(
            "Created encounter submission for claim {ClaimId}, deadline {Deadline}",
            claimId, submission.SubmissionDeadline);

        // TODO: persist to Cosmos DB via repository
        return Task.FromResult(submission);
    }

    public Task<EncounterSubmission?> GetByIdAsync(string id)
    {
        // TODO: implement Cosmos DB lookup
        _logger.LogInformation("GetByIdAsync called for {Id}", id);
        return Task.FromResult<EncounterSubmission?>(null);
    }

    public Task<IEnumerable<EncounterSubmission>> GetPendingByTenantAsync(string tenantId)
    {
        // TODO: query Cosmos DB for pending encounters by tenant
        _logger.LogInformation("GetPendingByTenantAsync called for tenant {TenantId}", tenantId);
        return Task.FromResult<IEnumerable<EncounterSubmission>>(Array.Empty<EncounterSubmission>());
    }

    public Task<IEnumerable<EncounterSubmission>> GetApproachingDeadlineAsync(int warningDays = 7)
    {
        // TODO: query across tenants for submissions where deadline is within warningDays
        _logger.LogInformation("GetApproachingDeadlineAsync called with {WarningDays} day threshold", warningDays);
        return Task.FromResult<IEnumerable<EncounterSubmission>>(Array.Empty<EncounterSubmission>());
    }

    public Task BatchAsync(IEnumerable<string> submissionIds, string batchId)
    {
        // TODO: update status to Batched, set BatchId
        _logger.LogInformation("Batching {Count} submissions into batch {BatchId}",
            submissionIds.Count(), batchId);
        return Task.CompletedTask;
    }

    public Task MarkSubmittedAsync(string batchId, DateTime submittedAt)
    {
        // TODO: update all submissions in batch to Submitted status
        _logger.LogInformation("Marking batch {BatchId} as submitted at {SubmittedAt}", batchId, submittedAt);
        return Task.CompletedTask;
    }

    public Task ProcessAcknowledgmentAsync(string batchId, string acknowledgmentCode, List<string>? errors = null)
    {
        // TODO: update submissions based on 999 acknowledgment
        _logger.LogInformation(
            "Processing 999 acknowledgment for batch {BatchId}: code={Code}, errors={ErrorCount}",
            batchId, acknowledgmentCode, errors?.Count ?? 0);
        return Task.CompletedTask;
    }
}
