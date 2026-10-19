namespace CloudHealthOffice.Portal.Services;

/// <summary>A note a reviewer wrote on a claim.</summary>
public sealed class ClaimNoteView
{
    public string Id { get; set; } = string.Empty;
    public string ClaimId { get; set; } = string.Empty;
    public string Category { get; set; } = ClaimNoteCategory.Internal;
    public string Text { get; set; } = string.Empty;
    public string AuthorId { get; set; } = string.Empty;
    public string? AuthorName { get; set; }
    public DateTime CreatedAt { get; set; }

    public string Author => string.IsNullOrWhiteSpace(AuthorName) ? AuthorId : AuthorName!;
}
