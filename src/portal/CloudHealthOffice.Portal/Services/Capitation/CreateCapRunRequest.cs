namespace CloudHealthOffice.Portal.Services;

public class CreateCapRunRequest
{
    public string RunType { get; set; } = "Monthly";
    public DateTime CapitationPeriod { get; set; }
    public CreateCapRunCriteria Criteria { get; set; } = new();
    public string? CreatedBy { get; set; }
    public string? Description { get; set; }
}
