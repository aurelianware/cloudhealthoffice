using System;
using System.ComponentModel.DataAnnotations;

namespace CoverageService.Models;

/// <summary>
/// Represents active health coverage linking Member → Sponsor → Benefit Plan.
/// Populated by X12 834 Enrollment transactions (HD/COB segments).
/// Critical for 270/271 eligibility checks and claims adjudication.
/// </summary>
public class Coverage
{
    /// <summary>
    /// Multi-tenant partition key (required for Cosmos DB isolation)
    /// </summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier (Cosmos DB document id)
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Member ID from Member Service (834 REF*0F)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    /// <summary>
    /// Group number from Sponsor Service (834 REF*1L)
    /// </summary>
    [Required]
    [StringLength(50)]
    public string GroupNumber { get; set; } = string.Empty;

    /// <summary>
    /// Benefit Plan ID from Benefit Plan Service
    /// </summary>
    [Required]
    [StringLength(50)]
    public string PlanId { get; set; } = string.Empty;

    /// <summary>
    /// Coverage level from 834 HD01 segment
    /// EMP = Employee Only, ESP = Employee and Spouse, ECH = Employee and Children, FAM = Family
    /// </summary>
    [StringLength(3)]
    public string? CoverageLevel { get; set; }

    /// <summary>
    /// Insurance line code from 834 INS05 segment
    /// HLT = Health, DEN = Dental, VIS = Vision, LIF = Life
    /// </summary>
    [StringLength(3)]
    public string? InsuranceLineCode { get; set; }

    /// <summary>
    /// Coverage effective date from 834 DTP*348 segment
    /// </summary>
    [Required]
    public DateTime EffectiveDate { get; set; }

    /// <summary>
    /// Coverage termination date from 834 DTP*349 segment
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    /// <summary>
    /// Current coverage status
    /// </summary>
    [Required]
    public CoverageStatus Status { get; set; } = CoverageStatus.Active;

    /// <summary>
    /// Line of Business (Commercial, Medicare, Medicaid, Exchange)
    /// Critical for determining applicable regulations and benefit rules
    /// </summary>
    [Required]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    /// <summary>
    /// Is this COBRA continuation coverage?
    /// </summary>
    public bool IsCOBRA { get; set; }

    /// <summary>
    /// COBRA effective date (if applicable)
    /// </summary>
    public DateTime? COBRAEffectiveDate { get; set; }

    /// <summary>
    /// Medicare coverage information (for COB)
    /// </summary>
    public MedicareCoverageInfo? MedicareCoverage { get; set; }

    /// <summary>
    /// Other insurance coverage (Coordination of Benefits)
    /// </summary>
    public OtherInsuranceInfo? OtherInsurance { get; set; }

    /// <summary>
    /// Monthly premium amount (employee contribution)
    /// </summary>
    public decimal? MonthlyPremium { get; set; }

    /// <summary>
    /// Employer contribution amount
    /// </summary>
    public decimal? EmployerContribution { get; set; }

    /// <summary>
    /// Maintenance type code from 834 INS03 segment
    /// 001 = Change, 021 = Addition, 024 = Cancellation, 030 = Audit
    /// </summary>
    [StringLength(3)]
    public string? MaintenanceTypeCode { get; set; }

    /// <summary>
    /// Maintenance reason code from 834 INS04 segment
    /// 25 = Change in Identifying Data, 32 = Divorce, 33 = Birth, etc.
    /// </summary>
    [StringLength(3)]
    public string? MaintenanceReasonCode { get; set; }

    // ── PCP (Primary Care Provider) Assignment ──

    /// <summary>
    /// NPI of the assigned Primary Care Provider (10-digit NPI)
    /// </summary>
    [StringLength(10)]
    public string? PcpNpi { get; set; }

    /// <summary>
    /// Denormalized PCP name for display (avoids provider-service round-trip)
    /// </summary>
    [StringLength(200)]
    public string? PcpName { get; set; }

    /// <summary>
    /// Date the PCP was assigned to the member on this coverage
    /// </summary>
    public DateTime? PcpAssignmentDate { get; set; }

    /// <summary>
    /// How the PCP was assigned
    /// </summary>
    public PcpAssignmentMethod? PcpAssignmentMethod { get; set; }

    /// <summary>
    /// Previous PCP NPI — retained for retro capitation adjustments when PCP changes
    /// </summary>
    [StringLength(10)]
    public string? PreviousPcpNpi { get; set; }

    // ── Audit ──

    /// <summary>
    /// Audit: Record creation timestamp
    /// </summary>
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Last modification timestamp
    /// </summary>
    public DateTime LastUpdatedDate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Audit: Created by user/system
    /// </summary>
    [StringLength(200)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Audit: Last updated by user/system
    /// </summary>
    [StringLength(200)]
    public string? LastUpdatedBy { get; set; }

    /// <summary>
    /// Check if coverage is active on a specific date
    /// </summary>
    public bool IsActiveOn(DateTime serviceDate)
    {
        return Status == CoverageStatus.Active
            && serviceDate >= EffectiveDate.Date
            && (!TerminationDate.HasValue || serviceDate <= TerminationDate.Value.Date);
    }
}

/// <summary>
/// Coverage status
/// </summary>
public enum CoverageStatus
{
    /// <summary>
    /// Coverage is active
    /// </summary>
    Active = 1,

    /// <summary>
    /// Coverage is pending (future effective date)
    /// </summary>
    Pending = 2,

    /// <summary>
    /// Coverage is terminated
    /// </summary>
    Terminated = 3,

    /// <summary>
    /// Coverage is suspended (grace period, non-payment)
    /// </summary>
    Suspended = 4,

    /// <summary>
    /// COBRA continuation (after employment termination)
    /// </summary>
    COBRA = 5
}

/// <summary>
/// Medicare coverage information for Coordination of Benefits
/// </summary>
public class MedicareCoverageInfo
{
    /// <summary>
    /// Medicare beneficiary identifier (MBI)
    /// </summary>
    [StringLength(20)]
    public string? MedicareBeneficiaryId { get; set; }

    /// <summary>
    /// Has Medicare Part A?
    /// </summary>
    public bool HasPartA { get; set; }

    /// <summary>
    /// Medicare Part A effective date
    /// </summary>
    public DateTime? PartAEffectiveDate { get; set; }

    /// <summary>
    /// Has Medicare Part B?
    /// </summary>
    public bool HasPartB { get; set; }

    /// <summary>
    /// Medicare Part B effective date
    /// </summary>
    public DateTime? PartBEffectiveDate { get; set; }

    /// <summary>
    /// Medicare is primary payer (true) or secondary (false)
    /// </summary>
    public bool IsPrimaryPayer { get; set; }
}

/// <summary>
/// Other insurance information for Coordination of Benefits (834 COB segment)
/// </summary>
public class OtherInsuranceInfo
{
    /// <summary>
    /// Other insurance payer name
    /// </summary>
    [StringLength(200)]
    public string? PayerName { get; set; }

    /// <summary>
    /// Other insurance policy number
    /// </summary>
    [StringLength(50)]
    public string? PolicyNumber { get; set; }

    /// <summary>
    /// Other insurance group number
    /// </summary>
    [StringLength(50)]
    public string? GroupNumber { get; set; }

    /// <summary>
    /// Other insurance effective date
    /// </summary>
    public DateTime? EffectiveDate { get; set; }

    /// <summary>
    /// Other insurance is primary (true) or secondary (false)
    /// </summary>
    public bool IsPrimaryPayer { get; set; }
}

/// <summary>
/// Common X12 834 coverage level codes
/// </summary>
public static class CoverageLevelCodes
{
    public const string EmployeeOnly = "EMP";
    public const string EmployeeAndSpouse = "ESP";
    public const string EmployeeAndChildren = "ECH";
    public const string Family = "FAM";
    public const string Individual = "IND";
    public const string EmployeePlusOne = "E1";
}

/// <summary>
/// Common X12 834 insurance line codes
/// </summary>
public static class InsuranceLineCodes
{
    public const string Health = "HLT";
    public const string Dental = "DEN";
    public const string Vision = "VIS";
    public const string Life = "LIF";
    public const string LongTermDisability = "LTD";
    public const string ShortTermDisability = "STD";
}

/// <summary>
/// Line of Business - determines regulatory requirements and benefit rules
/// </summary>
public enum LineOfBusiness
{
    Commercial = 1,
    Medicare = 2,
    Medicaid = 3,
    Exchange = 4,
    TRICARE = 5,
    VA = 6
}

/// <summary>
/// How the PCP was assigned to the member
/// </summary>
public enum PcpAssignmentMethod
{
    /// <summary>
    /// Auto-assigned by the system (e.g., geo-proximity, panel availability)
    /// </summary>
    AutoAssigned = 1,

    /// <summary>
    /// Member selected the PCP themselves
    /// </summary>
    MemberSelected = 2,

    /// <summary>
    /// Plan-level default PCP assignment
    /// </summary>
    PlanDefault = 3,

    /// <summary>
    /// Admin / back-office override (CSR, network ops). Distinct from
    /// <see cref="MemberSelected"/> for reporting — did the member choose or
    /// did an admin override? — and mirrors
    /// <c>PcpAssignmentSource.AdminAssigned</c> on the history row.
    /// </summary>
    Administrative = 4
}
