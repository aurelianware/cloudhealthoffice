namespace CloudHealthOffice.Portal.Services;

public class SponsorMemberView
{
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    /// <summary>LineOfBusiness enum name.</summary>
    public string LineOfBusiness { get; set; } = string.Empty;
    /// <summary>SponsorStatus enum name.</summary>
    public string Status { get; set; } = string.Empty;
    public ContactCard? PrimaryContact { get; set; }
    public BrokerCard? Broker { get; set; }
    public OpenEnrollmentCard? OpenEnrollment { get; set; }
}
