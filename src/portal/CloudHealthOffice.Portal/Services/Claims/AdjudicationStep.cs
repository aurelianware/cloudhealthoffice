namespace CloudHealthOffice.Portal.Services;

public class AdjudicationStep
{
    public string StepName { get; set; } = string.Empty;
    public int StepNumber { get; set; }
    public string Status { get; set; } = string.Empty; // Passed, Failed, Skipped, Warning
    public DateTime? Timestamp { get; set; }
    public int? DurationMs { get; set; }
    public string? Summary { get; set; }
    public string? ErrorDetail { get; set; }
}
