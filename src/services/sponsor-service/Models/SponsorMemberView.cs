namespace SponsorService.Models;

/// <summary>
/// Compact sponsor projection consumed by the portal Coverage tab's Sponsor
/// sub-section. Exposes only the fields the member-facing view needs —
/// billing totals and member counts stay on the richer
/// <c>/sponsors/{groupNumber}</c> and <c>/coverage-summary</c> endpoints.
/// </summary>
public class SponsorMemberView
{
    public string GroupNumber { get; set; } = string.Empty;
    public string SponsorName { get; set; } = string.Empty;
    public LineOfBusiness LineOfBusiness { get; set; }
    public SponsorStatus Status { get; set; }

    public ContactCard? PrimaryContact { get; set; }
    public BrokerCard? Broker { get; set; }
    public OpenEnrollmentCard? OpenEnrollment { get; set; }
}

public class ContactCard
{
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
}

public class BrokerCard
{
    public string? AgencyName { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Npn { get; set; }
}

public class OpenEnrollmentCard
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }

    /// <summary>
    /// One of <c>Upcoming</c> / <c>Open</c> / <c>Closed</c> — computed from
    /// now() at response time so this is always fresh.
    /// </summary>
    public string Status { get; set; } = nameof(OpenEnrollmentStatus.Closed);
}
