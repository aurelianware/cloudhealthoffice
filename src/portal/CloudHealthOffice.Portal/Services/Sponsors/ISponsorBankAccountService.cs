namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// Sponsor bank accounts in sponsor-service (<c>Services:SponsorService</c>),
/// under dual control. Reads are masked to the last 4.
/// </summary>
public interface ISponsorBankAccountService
{
    /// <summary>The approved account and pending change (masked); null when the sponsor does not exist.</summary>
    Task<SponsorBankAccountView?> GetBankAccountAsync(string groupNumber);
    Task<List<SponsorBankAccountChange>> GetChangesAsync(string groupNumber);
    Task<SponsorBankAccountChange> ProposeChangeAsync(string groupNumber, ProposeSponsorBankAccountRequest request);
    Task<SponsorBankAccountChange> ApproveChangeAsync(string groupNumber, string changeId, string? reason);
    Task<SponsorBankAccountChange> RejectChangeAsync(string groupNumber, string changeId, string? reason);
    Task<SponsorBankAccountChange> CancelChangeAsync(string groupNumber, string changeId, string? reason);
}
