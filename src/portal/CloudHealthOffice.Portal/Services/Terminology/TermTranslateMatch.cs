namespace CloudHealthOffice.Portal.Services;

public class TermTranslateMatch
{
    public string Equivalence { get; set; } = string.Empty;
    public TermCoding Concept { get; set; } = new();
    public bool IsContextResolved { get; set; }
    public bool IsOverride { get; set; }
    public string Source { get; set; } = string.Empty;
}
