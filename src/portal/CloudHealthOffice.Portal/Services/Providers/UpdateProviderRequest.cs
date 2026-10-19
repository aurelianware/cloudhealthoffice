namespace CloudHealthOffice.Portal.Services;

public class UpdateProviderRequest : CreateProviderRequest
{
    public string CredentialingStatus { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty;
}
