using System.Text.Json.Serialization;

namespace EligibilityService.Models;

/// <summary>
/// Tracks the lifecycle of a batch eligibility verification job.
/// Submitted via POST /api/v1/eligibility/batch; polled via
/// GET /api/v1/eligibility/batch/{jobId}.
/// </summary>
public class BatchEligibilityJob
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string TenantId { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BatchJobStatus Status { get; set; } = BatchJobStatus.Queued;

    /// <summary>
    /// Rows submitted by the caller (post-parse, pre-verify).
    /// </summary>
    public int TotalRows { get; set; }

    /// <summary>
    /// Rows for which verification has finished (success or row-level failure).
    /// </summary>
    public int ProcessedRows { get; set; }

    public int SucceededRows { get; set; }
    public int FailedRows { get; set; }

    /// <summary>
    /// URL (or relative path) where the result CSV can be downloaded once
    /// <see cref="Status"/> is Completed. Null while the job is still running.
    /// </summary>
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// First ~20 row-level errors, captured for quick diagnostics.
    /// Full errors live in the result file.
    /// </summary>
    public List<BatchRowError> Errors { get; set; } = new();

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime? StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }

    /// <summary>
    /// True when the batch exceeded the inline threshold and was queued onto
    /// Azure Service Bus for out-of-process processing.
    /// </summary>
    public bool Queued { get; set; }

    /// <summary>
    /// Where the input + result payloads live. Inline = embedded on the job
    /// document (or in-memory byte[] for the dev store). Blob = a separate
    /// Azure Blob object addressed by <see cref="InputBlobUri"/> /
    /// <see cref="ResultBlobUri"/>. Default Inline preserves existing behavior
    /// for the in-memory path.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BatchStorageMode StorageMode { get; set; } = BatchStorageMode.Inline;

    /// <summary>Set when <see cref="StorageMode"/> is Blob.</summary>
    public string? InputBlobUri { get; set; }

    /// <summary>Set when <see cref="StorageMode"/> is Blob.</summary>
    public string? ResultBlobUri { get; set; }
}

public enum BatchStorageMode
{
    Inline,
    Blob
}

public enum BatchJobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public class BatchRowError
{
    public int RowNumber { get; set; }
    public string? SubscriberId { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Single row parsed from the submitted CSV or JSON payload.
/// Either MemberId or SubscriberId must be present.
/// </summary>
public class BatchEligibilityRow
{
    public int RowNumber { get; set; }
    public string? MemberId { get; set; }
    public string? SubscriberId { get; set; }
    public DateTime ServiceDate { get; set; }

    /// <summary>
    /// The value forwarded to the adapter as <c>SubscriberId</c>.
    /// Prefer the caller-supplied SubscriberId so that memberId != subscriberId
    /// scenarios round-trip cleanly; fall back to MemberId only when no
    /// SubscriberId was provided.
    /// </summary>
    public string Identifier => !string.IsNullOrWhiteSpace(SubscriberId)
        ? SubscriberId!
        : MemberId ?? string.Empty;
}

public class BatchEligibilityResultRow
{
    public int RowNumber { get; set; }
    public string SubscriberId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public bool IsEligible { get; set; }
    public string StatusCode { get; set; } = string.Empty;
    public string? PlanId { get; set; }
    public string? GroupNumber { get; set; }
    public string? CoverageLevel { get; set; }
    public DateTime? CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }
    public string? Error { get; set; }
}
