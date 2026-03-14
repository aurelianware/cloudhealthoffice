using System.Text.Json.Serialization;
using EligibilityService.Models;

namespace EligibilityService.Adapters;

/// <summary>
/// Abstraction for eligibility verification platforms.
/// Each tenant can be configured to use a different adapter (CHO, Availity, Change Healthcare, etc.).
/// The adapter normalizes platform-specific responses into a common format.
/// </summary>
public interface IEligibilityAdapter
{
    /// <summary>
    /// Platform identifier matching EligibilityConfig.Platform on the tenant.
    /// </summary>
    string Platform { get; }

    /// <summary>
    /// Verify member eligibility and return coverage/benefit information.
    /// </summary>
    Task<EligibilityAdapterResponse> VerifyEligibilityAsync(
        EligibilityAdapterRequest request, CancellationToken ct = default);
}

/// <summary>
/// Normalized eligibility verification request sent to any adapter.
/// </summary>
public class EligibilityAdapterRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string SubscriberId { get; set; } = string.Empty;
    public string? MemberId { get; set; }
    public string? GroupNumber { get; set; }
    public string ProviderNPI { get; set; } = string.Empty;
    public string ServiceTypeCode { get; set; } = "30";
    public DateTime ServiceDate { get; set; } = DateTime.UtcNow;
    public DateTime? ServiceDateTo { get; set; }

    // Subscriber demographics (needed by some external platforms)
    public string? SubscriberFirstName { get; set; }
    public string? SubscriberLastName { get; set; }
    public DateTime? SubscriberDOB { get; set; }

    // Dependent (if checking dependent eligibility)
    public string? DependentFirstName { get; set; }
    public string? DependentLastName { get; set; }
    public DateTime? DependentDOB { get; set; }
    public string? DependentRelationship { get; set; }

    // Payer routing
    public string? PayerId { get; set; }
    public string? PayerName { get; set; }

    /// <summary>
    /// Platform-specific settings from EligibilityConfig.PlatformSettings.
    /// Adapters can read platform-specific configuration from here.
    /// </summary>
    public Dictionary<string, string> PlatformSettings { get; set; } = new();
}

/// <summary>
/// Normalized eligibility verification response from any adapter.
/// </summary>
public class EligibilityAdapterResponse
{
    public bool IsEligible { get; set; }
    public string StatusCode { get; set; } = string.Empty; // 1=Active, 6=Inactive
    public string? RejectionReason { get; set; }

    // Coverage details
    public string? CoverageLevel { get; set; }
    public string? PlanId { get; set; }
    public string? PlanName { get; set; }
    public string? GroupNumber { get; set; }
    public DateTime? CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    // Benefits
    public List<EligibilityBenefit> Benefits { get; set; } = new();

    // Accumulators
    public DeductibleInfo? Deductible { get; set; }
    public OutOfPocketInfo? OutOfPocket { get; set; }

    // COB
    public List<AdditionalInsurance>? AdditionalInsurances { get; set; }

    /// <summary>
    /// Raw response from the external platform, stored for audit/debugging.
    /// </summary>
    public string? RawResponse { get; set; }
}
