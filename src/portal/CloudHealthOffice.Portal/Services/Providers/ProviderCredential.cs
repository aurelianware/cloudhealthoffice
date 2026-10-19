namespace CloudHealthOffice.Portal.Services;

public class ProviderCredential
{
    public string CredentialType { get; set; } = string.Empty; // License, DEA, Board Certification
    public string Number { get; set; } = string.Empty;
    public string IssuingState { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; }
    public DateTime ExpirationDate { get; set; }
    public string Status { get; set; } = string.Empty; // Active, Expired, Suspended
}
