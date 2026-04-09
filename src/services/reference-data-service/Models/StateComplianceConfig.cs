using System.Text.Json.Serialization;

namespace ReferenceDataService.Models;

/// <summary>
/// State-specific compliance parameters embedded within a tenant's compliance configuration.
/// This is a value object — not a standalone Cosmos DB document.
/// Each state defines its own regulatory deadlines for prompt pay, prior authorization,
/// appeals, and encounter submission windows.
/// </summary>
public class StateComplianceConfig
{
    /// <summary>
    /// Maximum days to pay a clean electronic claim before prompt-pay penalties apply.
    /// FL Statute 627.6131: 35 days for electronic claims.
    /// </summary>
    [JsonPropertyName("promptPayElectronicDays")]
    public int PromptPayElectronicDays { get; set; }

    /// <summary>
    /// Maximum days to pay a clean paper claim before prompt-pay penalties apply.
    /// FL Statute 627.6131: 45 days for paper claims.
    /// </summary>
    [JsonPropertyName("promptPayPaperDays")]
    public int PromptPayPaperDays { get; set; }

    /// <summary>
    /// Annual interest rate applied as a penalty for late claim payments.
    /// FL Statute 627.6131: 10% per annum on overdue clean claims.
    /// </summary>
    [JsonPropertyName("promptPayPenaltyRateAnnual")]
    public decimal PromptPayPenaltyRateAnnual { get; set; }

    /// <summary>
    /// Days within which the payer must acknowledge receipt of a claim.
    /// FL: 0 (acknowledgment is not separately required by statute).
    /// </summary>
    [JsonPropertyName("claimAcknowledgmentDays")]
    public int ClaimAcknowledgmentDays { get; set; }

    /// <summary>
    /// Maximum hours to render an urgent prior authorization decision.
    /// FL Medicaid (SMMC Contract): 72 hours for urgent/concurrent requests.
    /// </summary>
    [JsonPropertyName("priorAuthUrgentHours")]
    public int PriorAuthUrgentHours { get; set; }

    /// <summary>
    /// Maximum calendar days to render a standard prior authorization decision.
    /// FL Medicaid (SMMC Contract): 5 calendar days for standard requests.
    /// </summary>
    [JsonPropertyName("priorAuthStandardDays")]
    public int PriorAuthStandardDays { get; set; }

    /// <summary>
    /// Maximum calendar days to resolve a standard (non-expedited) appeal.
    /// FL Medicaid (SMMC Contract): 30 calendar days for standard grievances/appeals.
    /// </summary>
    [JsonPropertyName("appealStandardDays")]
    public int AppealStandardDays { get; set; }

    /// <summary>
    /// Maximum hours to resolve an expedited appeal.
    /// FL Medicaid (SMMC Contract): 72 hours for expedited appeals.
    /// </summary>
    [JsonPropertyName("appealExpeditedHours")]
    public int AppealExpeditedHours { get; set; }

    /// <summary>
    /// Maximum days after adjudication to submit encounter data to the state agency.
    /// FL AHCA MCO contract: 60 days for encounter submission to FMMIS.
    /// </summary>
    [JsonPropertyName("encounterSubmissionDays")]
    public int EncounterSubmissionDays { get; set; }

    /// <summary>
    /// Returns a <see cref="StateComplianceConfig"/> pre-populated with
    /// Florida AHCA / SMMC 3.0 regulatory defaults.
    /// </summary>
    public static StateComplianceConfig Florida() => new()
    {
        PromptPayElectronicDays = 35,
        PromptPayPaperDays = 45,
        PromptPayPenaltyRateAnnual = 0.10m,
        ClaimAcknowledgmentDays = 0,
        PriorAuthUrgentHours = 72,
        PriorAuthStandardDays = 5,
        AppealStandardDays = 30,
        AppealExpeditedHours = 72,
        EncounterSubmissionDays = 60
    };
}
