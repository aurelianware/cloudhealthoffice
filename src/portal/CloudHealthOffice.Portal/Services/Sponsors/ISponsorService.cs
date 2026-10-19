namespace CloudHealthOffice.Portal.Services;

public interface ISponsorService
{
    Task<List<SponsorSummary>> SearchSponsorsAsync(string searchTerm);
    Task<SponsorDetails?> GetSponsorByIdAsync(string sponsorId);
    Task<string> CreateSponsorAsync(CreateSponsorRequest request);
    Task UpdateSponsorAsync(string sponsorId, UpdateSponsorRequest request);

    /// <summary>
    /// Compact sponsor projection consumed by the portal Coverage tab's
    /// Sponsor sub-section in MemberDetailsDialog. Returns null on 404.
    /// </summary>
    Task<SponsorMemberView?> GetSponsorMemberViewAsync(string groupNumber);
}
