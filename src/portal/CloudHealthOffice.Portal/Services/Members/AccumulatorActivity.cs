namespace CloudHealthOffice.Portal.Services;

public class AccumulatorActivity
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public DateTime OccurredAt { get; set; }
    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }
    public string? Reason { get; set; }
    public string ActorId { get; set; } = "system";
}
