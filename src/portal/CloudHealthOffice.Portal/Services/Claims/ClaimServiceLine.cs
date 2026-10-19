using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class ClaimServiceLine
{
    private List<string> _modifiers = new();
    private List<int> _diagnosisPointers = new();
    private List<ClaimLineAdjustment> _adjustments = new();

    public int LineNumber { get; set; }
    public string ProcedureCode { get; set; } = string.Empty;
    public string ProcedureDescription { get; set; } = string.Empty;
    public List<string> Modifiers
    {
        get => _modifiers;
        set => _modifiers = value ?? new();
    }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal Units { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal ChargeAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal AllowedAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal PaidAmount { get; set; }
    [JsonConverter(typeof(FlexibleDecimalJsonConverter))]
    public decimal PatientResponsibility { get; set; }
    public DateTime ServiceDateFrom { get; set; }
    public DateTime ServiceDateTo { get; set; }
    public string? RevenueCode { get; set; } // Institutional
    public List<int> DiagnosisPointers
    {
        get => _diagnosisPointers;
        set => _diagnosisPointers = value ?? new();
    }
    public List<ClaimLineAdjustment> Adjustments
    {
        get => _adjustments;
        set => _adjustments = value ?? new();
    }
    public string? LineStatus { get; set; }

    /// <summary>The line carries an adjudication result (claims-service <c>adjudicationResult</c>).</summary>
    [JsonIgnore]
    public bool IsAdjudicated { get; private set; }

    /// <summary>
    /// The line was denied: its status says so, or it was adjudicated to
    /// nothing allowed, paid or owed by the member on a billed charge.
    /// Derived for display ("Paid with line denials"), never a claim status.
    /// </summary>
    [JsonIgnore]
    public bool IsDenied =>
        string.Equals(LineStatus, "Denied", StringComparison.OrdinalIgnoreCase)
        || (IsAdjudicated && ChargeAmount > 0 && AllowedAmount == 0 && PaidAmount == 0 && PatientResponsibility == 0);

    [JsonPropertyName("adjudicationResult")]
    public ClaimLineAdjudicationProjection? AdjudicationResult
    {
        set
        {
            if (value is null)
            {
                return;
            }

            IsAdjudicated = true;
            AllowedAmount = value.AllowedAmount;
            PaidAmount = value.PaidAmount;
            PatientResponsibility = value.PatientResponsibility;
            if (_adjustments.Count == 0 && value.AdjustmentReasons.Count > 0)
            {
                _adjustments = value.AdjustmentReasons;
            }
        }
    }
}
