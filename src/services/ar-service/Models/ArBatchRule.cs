using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ArService.Models;

/// <summary>
/// Batch posting rule — QNXT equivalent. Controls how outputs from billing runs
/// and payment runs are automatically posted to GL accounts.
/// </summary>
public class ArBatchRule
{
    [Required]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [StringLength(50)]
    public string RuleCode { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string RuleName { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// What event fires this rule
    /// </summary>
    [Required]
    public BatchRuleTrigger Trigger { get; set; }

    public List<LineOfBusiness> ApplicableLobs { get; set; } = new();
    public List<string> ApplicablePlanIds { get; set; } = new();

    [Required]
    public string DebitAccountId { get; set; } = string.Empty;

    [Required]
    public string CreditAccountId { get; set; } = string.Empty;

    public BatchRuleSplitBehavior SplitBehavior { get; set; }

    /// <summary>
    /// Amounts below this are auto-approved
    /// </summary>
    public decimal? AutoApproveThreshold { get; set; }

    /// <summary>
    /// Execution order (lower numbers run first)
    /// </summary>
    public int ExecutionOrder { get; set; }

    [Required]
    public BatchRuleStatus Status { get; set; } = BatchRuleStatus.Active;

    public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    [StringLength(200)]
    public string? CreatedBy { get; set; }
}

public enum BatchRuleTrigger
{
    PremiumBillingRunComplete = 1,
    FfsPaymentRunComplete = 2,
    CapitationRunComplete = 3,
    EnrollmentTermination = 4,
    EnrollmentRetroChange = 5,
    ManualCashReceipt = 6
}

public enum BatchRuleSplitBehavior
{
    NoSplit = 0,
    SplitByAccountConfig = 1,
    SplitByPlan = 2
}

public enum BatchRuleStatus { Active = 1, Inactive = 2, Testing = 3 }
