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

    /// <summary>The EDI file record (<c>GET api/v1/enrollment/edi/files/{id}</c>), for a raw 834 upload.</summary>
    public string? EdiFileId { get; set; }

    /// <summary>The user (token subject) who ran the import.</summary>
    public string? ActorId { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public int MembersCreated { get; set; }
    public int MembersUpdated { get; set; }
    public int MembersTerminated { get; set; }
    public int DependentsCreated { get; set; }
    public int DependentsUpdated { get; set; }
    public int DependentsTerminated { get; set; }
    /// <summary>Attached dependents rejected (not counted in FailedCount; see ImportResult.DependentsFailed).</summary>
    public int DependentsFailed { get; set; }
    public int CoverageRecordsCreated { get; set; }
    public int CoverageRecordsUpdated { get; set; }
    public int CoverageRecordsReinstated { get; set; }
    public int CoverageRecordsTerminated { get; set; }
    public int CoverageMappingsUnresolved { get; set; }

    public List<string> Errors { get; set; } = new();

    // ── A raw 834 imported as a run (edi-operations.md §11) ──────────────

    /// <summary><see cref="EnrollmentImportRunStatus"/>. A run written at the end of a structured import is Completed when written.</summary>
    public string Status { get; set; } = EnrollmentImportRunStatus.Completed;

    /// <summary>Where the raw file is: the store's kind and the file's key.</summary>
    public string? RawStore { get; set; }
    public string? RawDocumentKey { get; set; }

    public string? Source { get; set; }
    public string? SourcePath { get; set; }

    /// <summary>Interchanges (0-based, file order) whose envelope was accepted; the rest are skipped. Null: all.</summary>
    public List<int>? AcceptedInterchanges { get; set; }

    /// <summary>ISA13s the envelope check claimed, given back if the file cannot be parsed.</summary>
    public List<ClaimedInterchangeReceipt> ClaimedReceipts { get; set; } = new();

    public string? InterchangeAcknowledgmentCode { get; set; }
    public string? AcknowledgmentTa1 { get; set; }
    public List<string> Ta1AcknowledgmentIds { get; set; } = new();

    /// <summary>
    /// The gather (edi-operations.md §11): how many units the parse produced,
    /// and how many have been applied or failed. The run finishes, once, when
    /// completed + failed reaches expected.
    /// </summary>
    public int? ExpectedUnits { get; set; }
    public int CompletedUnits { get; set; }
    public int FailedUnits { get; set; }

    /// <summary>Units before this sequence number are known to be applied or failed (the resume point).</summary>
    public int DispatchedThrough { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary><see cref="EnrollmentImportFailureKinds"/> when Status is Failed.</summary>
    public string? FailureKind { get; set; }
    public string? FailureMessage { get; set; }
}

public static class EnrollmentImportRunStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";

    public static bool IsFinished(string status) => status is Completed or Failed;
}

public static class EnrollmentImportFailureKinds
{
    /// <summary>The file is not a readable 834: nothing was imported and its ISA13 was given back.</summary>
    public const string Parse = "Parse";

    /// <summary>The stored raw file could not be read.</summary>
    public const string RawFileUnavailable = "RawFileUnavailable";

    /// <summary>The run was started and stopped more than the allowed number of times.</summary>
    public const string TooManyAttempts = "TooManyAttempts";
}

public sealed class ClaimedInterchangeReceipt
{
    public string PartyKey { get; set; } = string.Empty;
    public string ControlNumber { get; set; } = string.Empty;
    public DateTime ClaimedAt { get; set; }
}

/// <summary>
/// One unit of a run as applied: written before the unit is applied, so a
/// redelivered unit is never applied twice (edi-operations.md §11).
/// </summary>
public sealed class EnrollmentImportUnitRecord
{
    /// <summary><c>{runId}:{sequence}</c>.</summary>
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public string FilePosition { get; set; } = string.Empty;

    /// <summary>Applying, Done or Failed.</summary>
    public string Status { get; set; } = EnrollmentImportUnitStatus.Applying;
    /// <summary>Why it failed: the file position and a fixed text, never member data (as <c>ClientErrorText</c>).</summary>
    public string? Error { get; set; }

    /// <summary>The enrollment exception opened for a failed unit, where its change is worked.</summary>
    public string? ExceptionId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public static string IdFor(string runId, int sequence) => $"{runId}:{sequence}";
}

public static class EnrollmentImportUnitStatus
{
    public const string Applying = "Applying";
    public const string Done = "Done";
    public const string Failed = "Failed";
}
