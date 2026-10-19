namespace CloudHealthOffice.Portal.Services;

public class AddDependentPayload
{
    public AddDependentMember Member { get; set; } = new();
    public AddDependentRelationship Relationship { get; set; } = new();
}
