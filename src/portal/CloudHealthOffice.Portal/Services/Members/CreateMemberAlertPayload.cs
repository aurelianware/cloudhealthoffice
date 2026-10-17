namespace CloudHealthOffice.Portal.Services;

public class CreateMemberAlertPayload
{
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? RequiredAction { get; set; }
}
