using System.ComponentModel.DataAnnotations;

namespace EnrollmentImportService.Models;

/// <summary>
/// Persisted summary of one 834 import batch — the run-level counterpart to
/// <see cref="EnrollmentTransaction"/>'s per-member rows. Written once per
/// batch, at the end of <c>EnrollmentImportService.ImportEnrollmentAsync</c>,
/// from the same <see cref="ImportResult"/> already returned synchronously to
/// the caller; this is what lets that result be looked up again later instead
/// of only existing for the moment of the API call.
/// </summary>
public class EnrollmentImportRun
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string BatchId { get; set; } = string.Empty;

    public string? FileName { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MembersCreated { get; set; }
    public int MembersUpdated { get; set; }
    public int MembersTerminated { get; set; }
    public int DependentsCreated { get; set; }
    public int CoverageRecordsCreated { get; set; }
    public int CoverageMappingsUnresolved { get; set; }

    public List<string> Errors { get; set; } = new();
}
