namespace CloudHealthOffice.Portal.Services;

public class AccumulatorConfiguration
{
    public string ConfigId { get; set; } = string.Empty;
    public string PlanId { get; set; } = string.Empty;
    public decimal IndividualDeductible { get; set; }
    public decimal FamilyDeductible { get; set; }
    public decimal IndividualOopMax { get; set; }
    public decimal FamilyOopMax { get; set; }
    public bool PharmacyCrossAccumulatesDeductible { get; set; }
    public bool PharmacyCrossAccumulatesOop { get; set; }
    public bool DentalCrossAccumulatesOop { get; set; }
    public string EmbeddedOrAggregate { get; set; } = "Embedded"; // Embedded, Aggregate
}
