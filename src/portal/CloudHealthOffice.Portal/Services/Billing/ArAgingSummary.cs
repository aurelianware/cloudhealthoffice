namespace CloudHealthOffice.Portal.Services;

public class ArAgingSummary
{
    public decimal Current { get; set; }
    public decimal Days31To60 { get; set; }
    public decimal Days61To90 { get; set; }
    public decimal Days91To120 { get; set; }
    public decimal Over120Days { get; set; }
    public decimal TotalOutstanding { get; set; }
}
