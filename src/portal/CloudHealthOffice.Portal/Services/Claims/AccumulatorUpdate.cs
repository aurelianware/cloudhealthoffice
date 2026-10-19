namespace CloudHealthOffice.Portal.Services;

public class AccumulatorUpdate
{
    public string AccumulatorType { get; set; } = string.Empty; // IndividualDeductible, FamilyDeductible, IndividualOop, FamilyOop
    public decimal AmountApplied { get; set; }
    public decimal NewBalance { get; set; }
    public decimal Limit { get; set; }
}
