namespace CloudHealthOffice.Portal.Services;

public class AppealDetails
{
    /// <summary>The appeal number shown to people (the record id when it has none).</summary>
    public string AppealId { get; set; } = string.Empty;

    /// <summary>appeals-service's record id: what the access log is keyed on.</summary>
    public string RecordId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string AppealType { get; set; } = string.Empty;
    public string OriginalDecisionId { get; set; } = string.Empty;
    public string OriginalDecision { get; set; } = string.Empty;
    public string OriginalDenialReason { get; set; } = string.Empty;
    public string AppealReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsExpedited { get; set; }
    public DateTime FiledDate { get; set; }
    public DateTime DueDate { get; set; }
    public int DaysRemaining { get; set; }
    public string AssignedReviewer { get; set; } = string.Empty;
    public string ComplianceStatus { get; set; } = string.Empty;
    public string FinalDecision { get; set; } = string.Empty;
    public string FinalDecisionNotes { get; set; } = string.Empty;
    public DateTime? DecisionDate { get; set; }
    public List<AppealDocument> Documents { get; set; } = new();
    public List<AppealTimelineEvent> Timeline { get; set; } = new();
}
