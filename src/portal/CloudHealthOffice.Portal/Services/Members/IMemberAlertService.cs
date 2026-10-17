namespace CloudHealthOffice.Portal.Services;

public interface IMemberAlertService
{
    Task<List<MemberAlertView>> ListAsync(string memberId, bool activeOnly);
    Task<MemberAlertView?> CreateAsync(string memberId, CreateMemberAlertPayload payload);
    Task<MemberAlertView?> EndAsync(string memberId, string alertId);
}
