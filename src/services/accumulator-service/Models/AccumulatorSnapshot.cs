using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace AccumulatorService.Models;

/// <summary>
/// Aggregated plan-year accumulator state for a member. One snapshot per
/// (tenantId, memberId, planYearStart). Updated by ClaimFinalized events and
/// manual adjustments; served by GET /api/v1/accumulators/{memberId}.
///
/// The snapshot is the current-state projection. The source of truth for the
/// numbers is <see cref="AccumulatorEvent"/> — snapshot can be rebuilt by replay.
/// </summary>
[BsonIgnoreExtraElements]
public class AccumulatorSnapshot
{
    /// <summary>
    /// Document id: "{tenantId}:{memberId}:{planYearStart:yyyyMMdd}". Chosen so
    /// that retro claims for a prior plan year deterministically target the right
    /// snapshot without a lookup step.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [Required]
    public string TenantId { get; set; } = string.Empty;

    [Required]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public DateTime PlanYearStart { get; set; }

    [Required]
    public DateTime PlanYearEnd { get; set; }

    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal FamilyDeductibleUsed { get; set; }
    public decimal FamilyDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public decimal FamilyOopUsed { get; set; }
    public decimal FamilyOopLimit { get; set; }

    public List<ServiceAccumulator> ServiceAccumulators { get; set; } = new();

    /// <summary>Monotonic version counter. Incremented on every write; used for optimistic concurrency.</summary>
    public long Version { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    public static string BuildId(string tenantId, string memberId, DateTime planYearStart) =>
        $"{tenantId}:{memberId}:{planYearStart:yyyyMMdd}";
}

public class ServiceAccumulator
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal Used { get; set; }
    public decimal Limit { get; set; }

    /// <summary>USD | Visits | Days | Units.</summary>
    public string Unit { get; set; } = "USD";
}
