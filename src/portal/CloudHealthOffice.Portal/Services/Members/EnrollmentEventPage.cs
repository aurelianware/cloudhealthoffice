namespace CloudHealthOffice.Portal.Services;

public class EnrollmentEventPage
{
    public List<EnrollmentEvent> Items { get; set; } = new();
    public string? ContinuationToken { get; set; }
}
