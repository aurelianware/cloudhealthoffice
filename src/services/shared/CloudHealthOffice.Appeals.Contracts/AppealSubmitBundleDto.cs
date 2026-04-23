namespace CloudHealthOffice.Appeals.Contracts;

/// <summary>
/// Wire shape for the `$cho-appeal-submit` FHIR operation's Bundle input
/// after transformation into domain-layer calls. fhir-service builds an
/// <see cref="AppealSubmitBundleDto"/> from the inbound FHIR Bundle and
/// the HttpFhirAppealAdapter drives the appeals-service HTTP surface one
/// child at a time:
///  1. POST /api/appeals from <see cref="Appeal"/>.
///  2. POST /api/appeals/{id}/notes for each <see cref="Notes"/> entry.
///  3. POST /api/appeals/{id}/attachments for each <see cref="Attachments"/>.
///
/// Each child is submitted independently; partial failures surface as
/// OperationOutcome issues with retry-URLs per failed child. This is
/// documented as the atomicity caveat: the top-level appeal is created
/// atomically, but note/attachment appends happen serially with a
/// best-effort audit posture.
/// </summary>
public sealed class AppealSubmitBundleDto
{
    public required AppealDto Appeal { get; init; }
    public List<AppealNoteDto> Notes { get; init; } = new();
    public List<AppealAttachmentDto> Attachments { get; init; } = new();
}

/// <summary>
/// Outcome of one submit call, per-child. The operation controller
/// assembles these into a single FHIR OperationOutcome response.
/// </summary>
public sealed class AppealSubmitChildOutcome
{
    public required AppealSubmitChildKind Kind { get; init; }

    /// <summary>
    /// Stable identifier within the inbound Bundle — fullUrl for Bundle-
    /// entry children, position index for Notes/Attachments. Echoed back
    /// so the caller can correlate failures to specific entries.
    /// </summary>
    public required string ChildRef { get; init; }

    public bool Success { get; init; }

    /// <summary>
    /// Server-assigned id on success (appealId, noteId, attachmentId).
    /// </summary>
    public string? AssignedId { get; init; }

    /// <summary>
    /// HTTP status from the downstream appeals-service call. Null on
    /// network / timeout failures where no response was received.
    /// </summary>
    public int? HttpStatus { get; init; }

    /// <summary>
    /// Structural diagnostic excerpt — HTTP status code + error code
    /// string(s) extracted from a ProblemDetails body if present. PHI-
    /// adjacent strings (names, reasons, note text) are redacted before
    /// the excerpt is assembled by the adapter.
    /// </summary>
    public string? Diagnostics { get; init; }

    /// <summary>
    /// Distinguishes downstream 4xx (caller can adjust input and retry)
    /// from transient failures (network / timeout / 5xx, retry as-is).
    /// Maps to OperationOutcome.issue.code — `processing` vs `transient`.
    /// </summary>
    public AppealSubmitFailureKind FailureKind { get; init; }

    /// <summary>
    /// URL the caller can POST the same child to in order to retry.
    /// Populated on failure only.
    /// </summary>
    public string? RetryUrl { get; init; }
}

public enum AppealSubmitChildKind
{
    Appeal = 1,
    Note = 2,
    Attachment = 3
}

public enum AppealSubmitFailureKind
{
    /// <summary>No failure.</summary>
    None = 0,

    /// <summary>
    /// Downstream 4xx — the appeals-service rejected this child for
    /// domain reasons. Maps to OperationOutcome.issue.code = `processing`.
    /// </summary>
    Processing = 1,

    /// <summary>
    /// Network / timeout / 5xx — the appeals-service did not process
    /// this child successfully, retry may succeed. Maps to
    /// OperationOutcome.issue.code = `transient`.
    /// </summary>
    Transient = 2
}
