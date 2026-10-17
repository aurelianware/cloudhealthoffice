namespace CloudHealthOffice.Portal.Services;

public class PlanDocumentLink
{
    public string DocType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? ContentType { get; set; }
    public long? Size { get; set; }
    public string? ContentHashSha256 { get; set; }
    public string? Version { get; set; }
    public DateTime? EffectiveDate { get; set; }
}
