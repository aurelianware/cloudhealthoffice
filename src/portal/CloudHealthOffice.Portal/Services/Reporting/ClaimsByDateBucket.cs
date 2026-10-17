namespace CloudHealthOffice.Portal.Services;

public class ClaimsByDateBucket
{
    public DateTime Date { get; set; }
    public int Count { get; set; }
    public decimal TotalAmount { get; set; }
}
