namespace CloudHealthOffice.Portal.Services;

public class MemberNotePage
{
    public List<MemberNoteView> Items { get; set; } = new();
    public string? ContinuationToken { get; set; }
}
