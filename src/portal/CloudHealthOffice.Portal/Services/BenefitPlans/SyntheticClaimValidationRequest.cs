namespace CloudHealthOffice.Portal.Services;

public class SyntheticClaimValidationRequest
{
    public DateTime ServiceDate { get; set; }
    public string ProviderNpi { get; set; } = "1999999992";
    public string ProcedureCode { get; set; } = "99213";
    public decimal ChargeAmount { get; set; } = 150m;
}
