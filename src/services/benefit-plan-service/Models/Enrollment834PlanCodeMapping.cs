using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace BenefitPlanService.Models;

/// <summary>
/// Crosswalk from a trading partner's own 834 plan code to this platform's
/// canonical <see cref="BenefitPlan.PlanId"/>. Employers/payers assign plan
/// codes on their own terms (HD04, "PlanCoverageDescription" in the parsed
/// 834) per their trading-partner agreement — they have no reason to know,
/// and shouldn't be required to know, the PlanId this platform assigns
/// internally. enrollment-import-service resolves through this mapping
/// (keyed by group + insurance line + the partner's own code) instead of
/// writing that raw code straight into Coverage.PlanId.
/// </summary>
public class Enrollment834PlanCodeMapping
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>REF*1L group number the mapping applies to.</summary>
    [Required]
    [JsonPropertyName("groupNumber")]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>HD03 — HLT, DEN, VIS, etc.</summary>
    [Required]
    [JsonPropertyName("insuranceLineCode")]
    public string InsuranceLineCode { get; set; } = string.Empty;

    /// <summary>HD04 as the trading partner sends it — free text/code, not a PlanId.</summary>
    [Required]
    [JsonPropertyName("externalPlanCode")]
    public string ExternalPlanCode { get; set; } = string.Empty;

    /// <summary>The canonical <see cref="BenefitPlan.PlanId"/> this code resolves to.</summary>
    [Required]
    [JsonPropertyName("planId")]
    public string PlanId { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; set; } = string.Empty;
}
