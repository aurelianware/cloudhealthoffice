namespace CloudHealthOffice.Portal.Services;

public interface IAuthorizationService
{
    Task<List<AuthorizationSummary>> GetAuthorizationsAsync(string? memberId = null);
    Task<AuthorizationDetails?> GetAuthorizationByIdAsync(string authorizationId);
    Task<string> SubmitAuthorizationAsync(SubmitAuthorizationRequest request);
}
