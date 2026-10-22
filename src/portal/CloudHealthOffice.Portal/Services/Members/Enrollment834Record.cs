namespace CloudHealthOffice.Portal.Services;

public class Enrollment834Record
{
    public string TransactionId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MaintenanceTypeCode { get; set; } = string.Empty; // 001=Change, 021=Add, 024=Cancel
    /// <summary>The sponsor's maintenance reason (INS04).</summary>
    public string? MaintenanceReasonCode { get; set; }
    public string TransactionSetPurpose { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string Status { get; set; } = string.Empty; // Accepted, Rejected, Pending
    public string? EffectiveDate { get; set; }
    public string? TerminationDate { get; set; }
    public string? FileName { get; set; }

    /// <summary>The EDI file record this came in, for EDI Operations' file detail.</summary>
    public string? EdiFileId { get; set; }

    /// <summary>Where in the file: "INS 3", "INS 3, dependent 1".</summary>
    public string? FilePosition { get; set; }

    /// <summary>Why CHO did not apply it, or a note on an accepted row (e.g. TERM_SKIPPED_NOT_ON_FILE).</summary>
    public string? ReasonCode { get; set; }
    public List<Enrollment834Error> Errors { get; set; } = new();

    /// <summary>The reason in words for the member record.</summary>
    public string? ReasonText => ReasonCode switch
    {
        null or "" => null,
        "VALIDATION_FAILED" => "Failed validation",
        "SUBSCRIBER_NOT_FOUND" => "Subscriber not on file",
        "DEPENDENT_NOT_IDENTIFIED" => "Dependent could not be identified",
        "INVALID_TRANSACTION" => "Change not allowed",
        "DOWNSTREAM_REJECTED" => "Refused by a CHO service",
        "DOWNSTREAM_UNAVAILABLE" => "Service unavailable (can be reprocessed)",
        "CONCURRENT_UPDATE" => "Conflicting update (can be reprocessed)",
        "TERM_SKIPPED_NOT_ON_FILE" => "Not on file; nothing to terminate",
        "UNEXPECTED_ERROR" => "Unexpected error",
        _ => ReasonCode,
    };
}

public class Enrollment834Error
{
    public string Code { get; set; } = string.Empty;
    public string? Field { get; set; }
    public string Message { get; set; } = string.Empty;

    public override string ToString() => string.IsNullOrEmpty(Field) ? Message : $"{Field}: {Message}";
}
