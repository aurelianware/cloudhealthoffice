namespace CloudHealthOffice.Portal.Services;

public class CreateMemberNotePayload
{
    public string Category { get; set; } = "CustomerService";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? LinkedResourceType { get; set; }
    public string? LinkedResourceId { get; set; }
}
