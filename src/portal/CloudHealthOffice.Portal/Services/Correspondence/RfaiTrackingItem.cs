namespace CloudHealthOffice.Portal.Services;

public class RfaiTrackingItem
{
    public string RfaiId { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientType { get; set; } = string.Empty;
    public string RelatedClaimId { get; set; } = string.Empty;
    public string DocumentsRequested { get; set; } = string.Empty;
    public DateTime SentDate { get; set; }
    public DateTime ResponseDeadline { get; set; }
    public int DaysSinceSent { get; set; }
    public int DaysUntilDeadline { get; set; }
    public string Status { get; set; } = string.Empty;
}
