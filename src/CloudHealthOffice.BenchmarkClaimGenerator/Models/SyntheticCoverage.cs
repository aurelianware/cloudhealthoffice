namespace CloudHealthOffice.BenchmarkClaimGenerator.Models;

/// <summary>
/// Represents a member coverage record linking a member to a benefit plan.
/// Structurally compatible with the production Coverage entity in coverage-service.
/// </summary>
public class SyntheticCoverage
{
    /// <summary>Unique coverage record identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Tenant identifier for multi-tenant Cosmos DB.</summary>
    public string TenantId { get; set; } = "mcc-benchmark";

    /// <summary>Member identifier (FK to SyntheticMember or SyntheticDependent).</summary>
    public string MemberId { get; set; } = string.Empty;

    /// <summary>Subscriber identifier.</summary>
    public string SubscriberId { get; set; } = string.Empty;

    /// <summary>Group number from sponsor.</summary>
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>Benefit plan identifier (FK to SyntheticBenefitPlan).</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>Insurance line code: HLT (medical), DEN (dental), VIS (vision).</summary>
    public string InsuranceLineCode { get; set; } = "HLT";

    /// <summary>Coverage level code: EMP, ESP, ECH, FAM.</summary>
    public string CoverageLevelCode { get; set; } = "EMP";

    /// <summary>Coverage effective date (from 834 DTP*348).</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>Coverage termination date (from 834 DTP*349, null if active).</summary>
    public DateTime? TermDate { get; set; }

    /// <summary>Coverage status: Active, Terminated, Pending, Suspended, COBRA.</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Line of business: Medicaid, Commercial, Medicare, etc.</summary>
    public string LineOfBusiness { get; set; } = "Medicaid";

    /// <summary>X12 834 maintenance type code (021=Addition, 024=Cancel).</summary>
    public string MaintenanceTypeCode { get; set; } = "021";

    /// <summary>Primary care provider NPI (for HMO-style plans).</summary>
    public string? PcpNpi { get; set; }

    /// <summary>Primary care provider name (denormalized).</summary>
    public string? PcpName { get; set; }

    /// <summary>PCP assignment date.</summary>
    public DateTime? PcpAssignmentDate { get; set; }

    /// <summary>Monthly premium amount.</summary>
    public decimal? MonthlyPremium { get; set; }

    /// <summary>Whether this is a COBRA continuation coverage.</summary>
    public bool IsCOBRA { get; set; }

    /// <summary>Whether the member has other insurance (for COB scenarios).</summary>
    public bool HasOtherInsurance { get; set; }

    /// <summary>Other insurance payer name (for COB).</summary>
    public string? OtherInsurancePayerName { get; set; }

    /// <summary>Check if coverage is active on a given service date.</summary>
    public bool IsActiveOn(DateTime serviceDate)
    {
        return serviceDate >= EffectiveDate && (TermDate == null || serviceDate <= TermDate);
    }
}
