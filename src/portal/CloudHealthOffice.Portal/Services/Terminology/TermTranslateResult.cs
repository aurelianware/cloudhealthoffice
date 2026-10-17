namespace CloudHealthOffice.Portal.Services;

public class TermTranslateResult
{
    public bool Result { get; set; }
    public string? Message { get; set; }
    public List<TermTranslateMatch> Matches { get; set; } = new();
    public string? MapVersionId { get; set; }
    public DateTime TranslatedAt { get; set; }
}
