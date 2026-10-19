namespace CloudHealthOffice.Portal.Services;

public class CapRunSummary
{
    public string Id { get; set; } = string.Empty;
    public string RunNumber { get; set; } = string.Empty;
    public string RunType { get; set; } = "Monthly";
    public DateTime CapitationPeriod { get; set; }
    public string Status { get; set; } = "Pending";
    public string? LineOfBusiness { get; set; }
    public string? Description { get; set; }
    public CapRunCriteriaSummary? Criteria { get; set; }
    public int TotalStatements { get; set; }
    public int TotalMemberMonths { get; set; }
    public decimal TotalGrossCapitation { get; set; }
    public decimal TotalWithholds { get; set; }
    public decimal TotalNetPayable { get; set; }
    public int TotalProviders { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ExecutionStartedAt { get; set; }
    public DateTime? ExecutionCompletedAt { get; set; }
    public double? ExecutionDurationSeconds { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
