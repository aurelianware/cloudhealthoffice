namespace CloudHealthOffice.Portal.Services;

public class ServiceLineRequest
{
    public string ProcedureCode { get; set; } = string.Empty;
    public decimal ChargeAmount { get; set; }
    public int Units { get; set; } = 1;
}
