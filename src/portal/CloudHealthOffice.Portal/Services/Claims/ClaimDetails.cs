using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

public class ClaimDetails : ClaimSummary
{
    private List<ClaimDiagnosisCode> _diagnosisCodes = new();
    private List<ClaimServiceLine> _serviceLines = new();
    private List<ClaimAudit> _auditTrail = new();

    public string SubscriberId { get; set; } = string.Empty;
    public string? BenefitPlanId { get; set; }
    public string? NetworkTier { get; set; }
    public string SubscriberName { get; set; } = string.Empty;
    public string PatientName { get; set; } = string.Empty;
    public string PatientRelationship { get; set; } = string.Empty;
    public string? RenderingProviderName { get; set; }
    public string? RenderingProviderNPI { get; set; }
    public string? FacilityName { get; set; }
    public string? FacilityNPI { get; set; }
    public string PlaceOfService { get; set; } = string.Empty;
    public decimal DeductibleAmount { get; set; }
    public decimal CoinsuranceAmount { get; set; }
    public decimal CopayAmount { get; set; }
    public decimal PatientResponsibility { get; set; }
    public string? ClaimNotes { get; set; }
    public string? ReferralNumber { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public DateTime? PaidDate { get; set; }
    public string? CheckNumber { get; set; }
    public string? DenialReason { get; set; }
    public ClaimPendDetails? PendDetails { get; set; }
    public ClaimAiExamination? AiExamination { get; set; }
    protected override void OnAdjudicationResult(ClaimAdjudicationProjection value)
    {
        DeductibleAmount = value.DeductibleAmount;
        CoinsuranceAmount = value.CoinsuranceAmount;
        CopayAmount = value.CopayAmount;
        PatientResponsibility = value.PatientResponsibility;
        CheckNumber = value.CheckNumber;
        PaidDate = value.PaymentDate;
        DenialReason = value.DenialReason;
        NetworkTier = value.NetworkTier;
    }

    public List<ClaimDiagnosisCode> DiagnosisCodes
    {
        get => _diagnosisCodes;
        set => _diagnosisCodes = value ?? new();
    }
    public List<ClaimServiceLine> ServiceLines
    {
        get => _serviceLines;
        set => _serviceLines = value ?? new();
    }
    [JsonPropertyName("claimLines")]
    public List<ClaimServiceLine>? ClaimLines
    {
        get => _serviceLines;
        set => _serviceLines = value ?? new();
    }
    public ClaimAdjustmentInfo? AdjustmentInfo { get; set; }
    public bool IsEditable { get; set; }
    public bool CanApprove { get; set; }
    public bool CanDeny { get; set; }
    public bool CanReverse { get; set; }
    public List<ClaimAudit> AuditTrail
    {
        get => _auditTrail;
        set => _auditTrail = value ?? new();
    }
}
