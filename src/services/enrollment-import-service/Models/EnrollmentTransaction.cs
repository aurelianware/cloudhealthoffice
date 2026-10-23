using System.ComponentModel.DataAnnotations;

namespace EnrollmentImportService.Models;

/// <summary>
/// Single 834 transaction (one <c>MemberEnrollment</c> row out of a batch)
/// persisted for audit + member-service /834-transactions queries.
/// </summary>
public class EnrollmentTransaction
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string TransactionId { get; set; } = string.Empty;

    [Required]
    public string BatchId { get; set; } = string.Empty;

    [Required]
    public string MemberId { get; set; } = string.Empty;

    public string MemberName { get; set; } = string.Empty;

    public string MaintenanceTypeCode { get; set; } = string.Empty;

    public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    public string Status { get; set; } = "Accepted";

    public string? FileName { get; set; }

    /// <summary>The EDI file record (docs/architecture/edi-operations.md §3) of the file this came in; null for manual and JSON imports.</summary>
    public string? EdiFileId { get; set; }

    public string? SubscriberId { get; set; }

    /// <summary>The sponsor's maintenance reason (INS04), e.g. "07" termination of benefits.</summary>
    public string? MaintenanceReasonCode { get; set; }

    /// <summary>Coverage start and end the sponsor sent (DTP*356 / DTP*357), as received.</summary>
    public string? EffectiveDate { get; set; }
    public string? TerminationDate { get; set; }

    /// <summary>Where in the file: "INS 3", "INS 3, dependent 1", "dependent 2 without its subscriber".</summary>
    public string? FilePosition { get; set; }

    /// <summary>
    /// Why CHO did not apply it (<see cref="EnrollmentRejectionReasons"/>), or
    /// for an accepted row a note such as a term skipped because the member was
    /// not on file. Null for a plain acceptance.
    /// </summary>
    public string? ReasonCode { get; set; }

    /// <summary>The individual problems behind <see cref="ReasonCode"/>, e.g. each failed validation rule.</summary>
    public List<EnrollmentTransactionError> Errors { get; set; } = new();
}

public sealed class EnrollmentTransactionError
{
    public string Code { get; set; } = string.Empty;
    public string? Field { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Stable reason codes on a rejected (or noted) 834 transaction. The
/// enrollment exception queue (docs/architecture/enrollment-operations.md §3)
/// works from these.
/// </summary>
public static class EnrollmentRejectionReasons
{
    /// <summary>The transaction failed validation; <see cref="EnrollmentTransaction.Errors"/> lists each rule.</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>A change, reinstatement or dependent names a subscriber CHO does not have.</summary>
    public const string SubscriberNotFound = "SUBSCRIBER_NOT_FOUND";

    /// <summary>A dependent has no REF*23 and no first name + date of birth to key it by, or lacks REF*0F / INS03.</summary>
    public const string DependentNotIdentified = "DEPENDENT_NOT_IDENTIFIED";

    /// <summary>
    /// REF*1L is a policy number held by more than one group, so the file does
    /// not say which employer it is about. Reprocessable once the sending
    /// partner uses the group number, or once the duplicate policy number is
    /// resolved on the sponsor side.
    /// </summary>
    public const string AmbiguousSponsorIdentifier = "AMBIGUOUS_SPONSOR_IDENTIFIER";

    /// <summary>The transaction asked for something CHO refuses (unsupported maintenance type, bad date).</summary>
    public const string InvalidTransaction = "INVALID_TRANSACTION";

    /// <summary>member-, coverage- or sponsor-service refused the change.</summary>
    public const string DownstreamRejected = "DOWNSTREAM_REJECTED";

    /// <summary>A service needed to apply it could not be reached; safe to reprocess.</summary>
    public const string DownstreamUnavailable = "DOWNSTREAM_UNAVAILABLE";

    /// <summary>Another change to the same member was being applied at the same time; safe to reprocess.</summary>
    public const string ConcurrentUpdate = "CONCURRENT_UPDATE";

    /// <summary>Anything else; details are in the service log.</summary>
    public const string UnexpectedError = "UNEXPECTED_ERROR";

    /// <summary>
    /// The import stopped while this unit of a run was being applied
    /// (edi-operations.md §11): it may be partly applied, so it was not
    /// applied again. Check the member before reprocessing.
    /// </summary>
    public const string UnitInterrupted = "UNIT_INTERRUPTED";

    /// <summary>Accepted, but nothing was changed: a termination for a member not on file.</summary>
    public const string TermSkippedNotOnFile = "TERM_SKIPPED_NOT_ON_FILE";
}
