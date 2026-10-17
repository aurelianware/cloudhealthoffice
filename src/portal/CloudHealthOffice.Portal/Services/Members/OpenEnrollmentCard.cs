namespace CloudHealthOffice.Portal.Services;

public class OpenEnrollmentCard
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    /// <summary>"Upcoming" / "Open" / "Closed" — server-computed.</summary>
    public string Status { get; set; } = "Closed";
}
