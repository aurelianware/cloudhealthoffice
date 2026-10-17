namespace CloudHealthOffice.Portal.Services;

public class MemberNoteView
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Category { get; set; } = "CustomerService";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string? LinkedResourceType { get; set; }
    public string? LinkedResourceId { get; set; }
}
