namespace CloudHealthOffice.Portal.Services;

public class ClaimDuplicateLineMatch
{
    public int LineNumber { get; set; }
    public int? MatchedLineNumber { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public List<string> MatchedFields { get; set; } = new();
    public string? Message { get; set; }
}
