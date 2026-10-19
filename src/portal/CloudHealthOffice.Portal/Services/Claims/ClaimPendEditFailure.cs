namespace CloudHealthOffice.Portal.Services;

public class ClaimPendEditFailure
{
    public string EditType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? Column1Code { get; set; }
    public string? Column2Code { get; set; }
    public List<int> AffectedLineNumbers { get; set; } = new();
    public bool ModifierOverridePresent { get; set; }
    public string? SuggestedCarc { get; set; }
    public string? SuggestedRarc { get; set; }
}
