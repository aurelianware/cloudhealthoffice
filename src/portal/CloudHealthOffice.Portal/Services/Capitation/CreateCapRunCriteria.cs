namespace CloudHealthOffice.Portal.Services;

public class CreateCapRunCriteria
{
    public string LineOfBusiness { get; set; } = "Commercial";
    public string? ProviderNPI { get; set; }
    public string? ContractType { get; set; }
    public DateTime? OriginalPeriod { get; set; }
}
