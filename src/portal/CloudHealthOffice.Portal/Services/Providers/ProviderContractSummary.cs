namespace CloudHealthOffice.Portal.Services;

public class ProviderContractSummary
{
    public string Id { get; set; } = string.Empty;
    public string ContractNumber { get; set; } = string.Empty;
    public string ProviderNPI { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string? ProviderTin { get; set; }
    public string ProviderType { get; set; } = "Individual";
    public string LineOfBusiness { get; set; } = "Commercial";
    public List<string> PlanIds { get; set; } = new();
    public string PaymentMethodology { get; set; } = "FullCapitation";
    public string NetworkStatus { get; set; } = "Participating";
    public string? ContractOwner { get; set; }
    public string? SignatoryName { get; set; }
    public DateTime? SignedDate { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public string? TerminationReason { get; set; }
    public bool AutoRenews { get; set; }
    public int? RenewalTermMonths { get; set; }
    public int? NoticeRequiredDays { get; set; }
    public List<ContractAmendmentSummary> Amendments { get; set; } = new();
    public string Status { get; set; } = "Draft";
    public List<string> CapitationRateConfigIds { get; set; } = new();
    public List<string> FfsRateConfigIds { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}
