namespace CloudHealthOffice.Portal.Services;

public class UpdateRelationshipPayload
{
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool? IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
}
