namespace CloudHealthOffice.Portal.Services;

public class MedicalCodeDetails : MedicalCode
{
    public string LongDescription { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
    public List<RelatedCode> RelatedCodes { get; set; } = new();
    public string? ParentCode { get; set; }
    public List<string> ChildCodes { get; set; } = new();
    public bool RequiresPriorAuth { get; set; }
    public string? ClinicalNotes { get; set; }
}
