namespace CloudHealthOffice.Portal.Services;

public class CapDisbursementBatchResult
{
    public int TotalStatements { get; set; }
    public int DisbursementsInitiated { get; set; }
    public int Skipped { get; set; }
    public int Errors { get; set; }
    public decimal TotalAmount { get; set; }
    public List<string> ErrorMessages { get; set; } = new();

    /// <summary>The NACHA credit file capitation-service sent to the bank: masked summary and receipt.</summary>
    public NachaFileResult? NachaFile { get; set; }
}
