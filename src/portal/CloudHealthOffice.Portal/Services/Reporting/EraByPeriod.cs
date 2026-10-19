namespace CloudHealthOffice.Portal.Services;

public class EraByPeriod
{
    public string Period { get; set; } = string.Empty; // YYYY-MM
    public int EraCount { get; set; }
    public decimal TotalAmount { get; set; }
}
