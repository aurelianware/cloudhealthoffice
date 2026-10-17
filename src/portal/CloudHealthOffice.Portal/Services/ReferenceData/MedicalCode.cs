namespace CloudHealthOffice.Portal.Services;

public class MedicalCode
{
    public string CodeSystem { get; set; } = string.Empty; // CPT, ICD-10-CM, HCPCS, Revenue, etc.
    public string Code { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Status { get; set; } = string.Empty; // Active, Deprecated
}
