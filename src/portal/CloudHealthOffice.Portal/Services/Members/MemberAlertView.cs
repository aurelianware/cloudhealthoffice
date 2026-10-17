namespace CloudHealthOffice.Portal.Services;

public class MemberAlertView
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RequiredAction { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string? EndedBy { get; set; }

    public bool IsActive(DateTime? asOf = null)
    {
        var t = asOf ?? DateTime.UtcNow;
        return StartDate <= t && (!EndDate.HasValue || EndDate.Value > t);
    }
}
