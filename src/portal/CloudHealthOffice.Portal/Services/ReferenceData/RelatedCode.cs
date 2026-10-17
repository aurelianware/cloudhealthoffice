namespace CloudHealthOffice.Portal.Services;

public class RelatedCode
{
    public string CodeSystem { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RelationType { get; set; } = string.Empty; // CrossReference, Alternative, Replacement
}
