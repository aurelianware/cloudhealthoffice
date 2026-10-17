namespace CloudHealthOffice.Portal.Services;

public class AddDependentRelationship
{
    public string RelationshipCode { get; set; } = "19";
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
}
