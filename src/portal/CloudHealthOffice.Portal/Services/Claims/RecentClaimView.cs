namespace CloudHealthOffice.Portal.Services;

/// <summary>A claim the user opened recently (Home "Recent").</summary>
public sealed class RecentClaimView
{
    public string ClaimId { get; set; } = string.Empty;
    public string? ClaimNumber { get; set; }
    public string? MemberId { get; set; }
    public DateTime ViewedAt { get; set; }
}
