namespace CloudHealthOffice.Portal.Services;

public class GracePeriodState
{
    public bool IsInGrace { get; set; }
    /// <summary>"Standard" or "AptcThreeMonth".</summary>
    public string GraceType { get; set; } = "Standard";
    public int DaysRemaining { get; set; }
    public DateTime? ExpiresOn { get; set; }
}
