namespace CloudHealthOffice.Portal.Services;

public class MemberBenefitCostSharing
{
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOutOfPocketMax { get; set; }
    public decimal FamilyOutOfPocketMax { get; set; }
    public decimal InNetworkDeductible { get; set; }
    public decimal OutOfNetworkDeductible { get; set; }
    public decimal InNetworkOutOfPocketMax { get; set; }
    public decimal OutOfNetworkOutOfPocketMax { get; set; }
}
