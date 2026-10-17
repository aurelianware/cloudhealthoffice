namespace CloudHealthOffice.Portal.Services;

public class FamilyRelationshipRow
{
    public string Id { get; set; } = string.Empty;
    public string SubjectMemberId { get; set; } = string.Empty;
    public string RelatedMemberId { get; set; } = string.Empty;
    public string RelationshipCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
    public DateTime? DeletedAt { get; set; }
}
