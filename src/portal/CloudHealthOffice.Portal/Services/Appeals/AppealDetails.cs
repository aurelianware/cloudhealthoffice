namespace CloudHealthOffice.Portal.Services;

public class AppealDetails
{
    public string AppealId { get; set; } = string.Empty;
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
