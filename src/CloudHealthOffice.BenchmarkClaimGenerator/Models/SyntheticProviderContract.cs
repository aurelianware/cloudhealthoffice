namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a provider contract with fee schedule assignment.
/// Structurally compatible with the production ProviderContract entity.
/// </summary>
public class SyntheticProviderContract
{
    /// <summary>Unique contract identifier (e.g., MCC-CTR-0000001).</summary>
    public string ContractId { get; set; } = string.Empty;

    /// <summary>Tenant identifier for multi-tenant Cosmos DB.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Contract number (format: CTR-{NPI}-{Year}).</summary>
    public string ContractNumber { get; set; } = string.Empty;

    /// <summary>Provider NPI (FK to SyntheticProvider).</summary>
    public string ProviderNpi { get; set; } = string.Empty;

    /// <summary>Provider name (denormalized).</summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Provider type: Individual or Organization.</summary>
    public string ProviderType { get; set; } = "Individual";

    /// <summary>Line of business for this contract.</summary>
    public string LineOfBusiness { get; set; } = "Medicaid";

    /// <summary>Fee schedule identifier (FK to SyntheticFeeSchedule).</summary>
    public string FeeScheduleId { get; set; } = string.Empty;

    /// <summary>Contract type: FeeForService, Capitation, PerDiem.</summary>
    public string ContractType { get; set; } = "FeeForService";

    /// <summary>Payment methodology: FeeForService, FullCapitation, Hybrid, GlobalRisk.</summary>
    public string PaymentMethodology { get; set; } = "FeeForService";

    /// <summary>Network participation status: Participating, NonParticipating, TieredException.</summary>
    public string NetworkStatus { get; set; } = "Participating";

    /// <summary>Contract effective date.</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>Contract termination date (null if open-ended).</summary>
    public DateTime? TermDate { get; set; }

    /// <summary>Whether the contract auto-renews.</summary>
    public bool AutoRenews { get; set; } = true;

    /// <summary>Contract status: Draft, Active, Suspended, Terminated, Expired.</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Reimbursement method description.</summary>
    public string ReimbursementMethod { get; set; } = "PercentOfFeeSchedule";

    /// <summary>Per-member-per-month rate for capitated contracts.</summary>
    public decimal? CapitationPmpm { get; set; }

    /// <summary>Per diem rate for per-diem contracts.</summary>
    public decimal? PerDiemRate { get; set; }
}
