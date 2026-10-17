namespace CloudHealthOffice.Portal.Services;

public class PharmacyDetail
{
    /// <summary>Verbatim plan ServiceCategory. Display this in the UI.</summary>
    public string? TierLabel { get; set; }

    /// <summary>Normalized bucket for analytics (Tier1/Tier2/.../Specialty). Do not display.</summary>
    public string? CanonicalTier { get; set; }

    public bool IsSpecialty { get; set; }
}
