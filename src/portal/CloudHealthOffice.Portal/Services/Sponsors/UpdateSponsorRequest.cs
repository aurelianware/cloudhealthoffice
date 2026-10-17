namespace CloudHealthOffice.Portal.Services;

public class UpdateSponsorRequest : CreateSponsorRequest
{
    public string Status { get; set; } = string.Empty;
    public DateTime? ContractEndDate { get; set; }
}
