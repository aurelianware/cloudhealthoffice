namespace CloudHealthOffice.Portal.Services;

public class AssignPcpRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderNpi { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }

    /// <summary>MemberChoice (default), AutoAssigned, or AdminAssigned.</summary>
    public string? AssignmentSource { get; set; }
}
