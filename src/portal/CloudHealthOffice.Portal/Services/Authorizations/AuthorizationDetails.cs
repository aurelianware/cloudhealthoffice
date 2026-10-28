namespace CloudHealthOffice.Portal.Services;

public class AuthorizationDetails : AuthorizationSummary
{
    public string ProviderId { get; set; } = string.Empty;
    public string DiagnosisCode { get; set; } = string.Empty;
    public string DiagnosisDescription { get; set; } = string.Empty;
    public string ProcedureCode { get; set; } = string.Empty;
    public string ProcedureDescription { get; set; } = string.Empty;
    public int UnitsRequested { get; set; }
    public int? UnitsApproved { get; set; }
    public DateTime ServiceStartDate { get; set; }
    public DateTime? ServiceEndDate { get; set; }
    public string ReviewerNotes { get; set; } = string.Empty;
    public string DenialReason { get; set; } = string.Empty;
    public List<AttachmentInfo> Attachments { get; set; } = new();

    /// <summary>"Medicare", "Medicaid", "Commercial"... as authorization-service writes it.</summary>
    public string? LineOfBusiness { get; set; }
    /// <summary>The decision deadline in force, including a lawful extension.</summary>
    public DateTime? DecisionDueAt { get; set; }
    public DecisionExtensionView? DecisionExtension { get; set; }
    /// <summary>The record's revision, for the timeframe actions to reload against.</summary>
    public long Revision { get; set; }
}

/// <summary>The one lawful extension of an open request's decision timeframe (CMS-0057-F).</summary>
public class DecisionExtensionView
{
    public DateTime GrantedAt { get; set; }
    public string GrantedBy { get; set; } = string.Empty;
    public DateTime OriginalDueAt { get; set; }
    public DateTime ExtendedDueAt { get; set; }
    public string Basis { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public ExtensionMemberNoticeView? MemberNotice { get; set; }
}

public class ExtensionMemberNoticeView
{
    public string LetterId { get; set; } = string.Empty;
    public DateTime RecordedAt { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
}

/// <summary>The outcome of a timeframe action: the reloaded record, or the service's reason for refusing.</summary>
public sealed record AuthorizationActionResult(AuthorizationDetails? Authorization, string? Error)
{
    public bool Succeeded => Error is null;
}
