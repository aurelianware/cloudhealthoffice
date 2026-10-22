namespace CloudHealthOffice.Infrastructure.Work;

/// <summary>
/// One enrollment exception that needs a specialist, as enrollment-import-service
/// reports it to work-management-service (GET api/v1/enrollment/exceptions/work-snapshots).
/// Carries what routing and priority need and no more: no names, no demographics.
/// </summary>
public sealed class EnrollmentWorkSnapshot
{
    public string ExceptionId { get; set; } = string.Empty;

    /// <summary>"EX-7F3A9C21": the reference shown on the work item.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>EnrollmentRejectionReasons code, e.g. SUBSCRIBER_NOT_FOUND. Queues route on it.</summary>
    public string ReasonCode { get; set; } = string.Empty;

    public string? GroupNumber { get; set; }
    public string? MemberId { get; set; }
    public string? MaintenanceTypeCode { get; set; }

    /// <summary>When the exception was first raised (the item's age starts here).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Ticks of the exception's last change; a later snapshot supersedes an earlier one.</summary>
    public long SourceVersion { get; set; }
}

public sealed class EnrollmentWorkSnapshotPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public long TotalCount { get; set; }
    public DateTime ObservedAt { get; set; }
    public List<EnrollmentWorkSnapshot> Items { get; set; } = new();
}
