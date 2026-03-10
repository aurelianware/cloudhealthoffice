using Microsoft.AspNetCore.Mvc;
using CoverageService.Middleware;
using CoverageService.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace CoverageService.Controllers;

/// <summary>
/// Coverage management API - links Member → Sponsor → Benefit Plan.
/// Data populated by X12 834 Enrollment transactions (HD/COB segments).
/// Critical for 270/271 eligibility checks and claims processing.
/// </summary>
[ApiController]
[Route("api/v1/coverage")]
public class CoverageController : ControllerBase
{
    // Tenant context from middleware
    private string TenantId => HttpContext.GetTenantId();

    // TODO: Replace with actual repository/service injection
    // private readonly ICoverageRepository _coverageRepository;
    // public CoverageController(ICoverageRepository coverageRepository)
    // {
    //     _coverageRepository = coverageRepository;
    // }

    /// <summary>
    /// Search coverage records by various criteria
    /// </summary>
    /// <param name="memberId">Filter by member ID</param>
    /// <param name="groupNumber">Filter by sponsor group number</param>
    /// <param name="planId">Filter by benefit plan ID</param>
    /// <param name="lineOfBusiness">Filter by line of business</param>
    /// <param name="activeOnly">Return only active coverage</param>
    /// <param name="asOfDate">Check coverage active as of specific date</param>
    /// <param name="pageSize">Page size (max 100)</param>
    /// <param name="continuationToken">Continuation token for pagination</param>
    [HttpGet]
    [ProducesResponseType(typeof(CoverageListResponse), 200)]
    public async Task<IActionResult> SearchCoverage(
        [FromQuery] string? memberId = null,
        [FromQuery] string? groupNumber = null,
        [FromQuery] string? planId = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery] DateTime? asOfDate = null,
        [FromQuery][Range(1, 100)] int pageSize = 20,
        [FromQuery] string? continuationToken = null)
    {
        // TODO: Implement with Cosmos DB query

        // Mock response
        var mockCoverage = new List<Coverage>
        {
            new Coverage
            {
                TenantId = TenantId,
                Id = "cov-001",
                MemberId = "MEM123456789",
                GroupNumber = "GRP-12345",
                PlanId = "PLAN-GOLD-HMO-001",
                CoverageLevel = CoverageLevelCodes.Family,
                InsuranceLineCode = InsuranceLineCodes.Health,
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                Status = CoverageStatus.Active,
                MonthlyPremium = 500.00m,
                EmployerContribution = 1200.00m
            }
        };

        return Ok(new CoverageListResponse
        {
            Coverage = mockCoverage,
            ContinuationToken = null,
            TotalCount = 1
        });
    }

    /// <summary>
    /// Get coverage details by ID
    /// </summary>
    /// <param name="id">Coverage ID</param>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Coverage), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetCoverage([FromRoute] string id)
    {
        // TODO: Implement with Cosmos DB query

        var coverage = new Coverage
        {
            TenantId = TenantId,
            Id = id,
            MemberId = "MEM123456789",
            GroupNumber = "GRP-12345",
            PlanId = "PLAN-GOLD-HMO-001",
            CoverageLevel = CoverageLevelCodes.Family,
            InsuranceLineCode = InsuranceLineCodes.Health,
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            TerminationDate = null,
            Status = CoverageStatus.Active,
            IsCOBRA = false,
            MonthlyPremium = 500.00m,
            EmployerContribution = 1200.00m,
            MaintenanceTypeCode = "021",  // Addition
            MaintenanceReasonCode = "33"   // New enrollment
        };

        return Ok(coverage);
    }

    /// <summary>
    /// Get active coverage for a member (critical for eligibility checks)
    /// </summary>
    /// <param name="memberId">Member ID</param>
    /// <param name="serviceDate">Service date to check (defaults to today)</param>
    /// <param name="insuranceLineCode">Filter by insurance line (HLT, DEN, VIS)</param>
    [HttpGet("member/{memberId}/active")]
    [ProducesResponseType(typeof(List<Coverage>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetActiveCoverage(
        [FromRoute] string memberId,
        [FromQuery] DateTime? serviceDate = null,
        [FromQuery] string? insuranceLineCode = null)
    {
        var checkDate = serviceDate ?? DateTime.UtcNow.Date;

        // TODO: Query coverage where:
        // - MemberId = memberId
        // - TenantId = TenantId
        // - Status = Active (or check IsActiveOn(checkDate))
        // - Optional: InsuranceLineCode filter

        var activeCoverage = new List<Coverage>
        {
            new Coverage
            {
                TenantId = TenantId,
                MemberId = memberId,
                GroupNumber = "GRP-12345",
                PlanId = "PLAN-GOLD-HMO-001",
                CoverageLevel = CoverageLevelCodes.Family,
                InsuranceLineCode = InsuranceLineCodes.Health,
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                Status = CoverageStatus.Active
            }
        };

        if (!string.IsNullOrEmpty(insuranceLineCode))
        {
            activeCoverage = activeCoverage
                .Where(c => c.InsuranceLineCode == insuranceLineCode)
                .ToList();
        }

        if (!activeCoverage.Any())
        {
            return NotFound(new
            {
                MemberId = memberId,
                ServiceDate = checkDate,
                Message = "No active coverage found for member on service date"
            });
        }

        return Ok(activeCoverage);
    }

    /// <summary>
    /// Get coverage history for a member
    /// </summary>
    /// <param name="memberId">Member ID</param>
    /// <param name="includeTerminated">Include terminated coverage</param>
    [HttpGet("member/{memberId}/history")]
    [ProducesResponseType(typeof(List<Coverage>), 200)]
    public async Task<IActionResult> GetCoverageHistory(
        [FromRoute] string memberId,
        [FromQuery] bool includeTerminated = true)
    {
        // TODO: Query all coverage for member, ordered by EffectiveDate DESC

        var history = new List<Coverage>
        {
            new Coverage
            {
                MemberId = memberId,
                GroupNumber = "GRP-12345",
                PlanId = "PLAN-GOLD-HMO-001",
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                Status = CoverageStatus.Active
            },
            new Coverage
            {
                MemberId = memberId,
                GroupNumber = "GRP-12345",
                PlanId = "PLAN-SILVER-PPO-001",
                EffectiveDate = DateTime.UtcNow.AddYears(-2),
                TerminationDate = DateTime.UtcNow.AddMonths(-6).AddDays(-1),
                Status = CoverageStatus.Terminated
            }
        };

        if (!includeTerminated)
        {
            history = history.Where(c => c.Status != CoverageStatus.Terminated).ToList();
        }

        return Ok(history);
    }

    /// <summary>
    /// Get Coordination of Benefits (COB) entries for a member.
    ///
    /// Returns all other-insurance and Medicare records on the member's coverage,
    /// ordered by payer sequence (Primary first). Used by:
    ///   - Eligibility service when building the 271 response (SB/OI loops)
    ///   - Claims intake to populate CobInfo on secondary/tertiary claims
    ///   - Portal to display "Other Insurance" for member management
    ///
    /// Source: Coverage.OtherInsurance and Coverage.MedicareCoverage fields,
    /// populated via the 834 COB segment during enrollment.
    /// </summary>
    [HttpGet("member/{memberId}/cob")]
    [ProducesResponseType(typeof(List<CobEntryResponse>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetCobEntries(
        [FromRoute] string memberId,
        [FromQuery] DateTime? asOfDate = null)
    {
        var checkDate = asOfDate ?? DateTime.UtcNow;

        // TODO: Query Cosmos/Mongo for active coverage records where MemberId = memberId
        // and TenantId = TenantId, then project OtherInsurance and MedicareCoverage into
        // CobEntryResponse objects.

        // Stub response — matches the shape the eligibility service expects
        var cobEntries = new List<CobEntryResponse>();

        // Example: member has secondary Medicare
        cobEntries.Add(new CobEntryResponse
        {
            PayerName        = "Example Primary Payer",
            PayerId          = "PAYER01",
            CoverageSequence = "P",
            GroupNumber      = "GRP-PRIMARY",
            CoverageBeginDate = DateTime.UtcNow.AddYears(-2).Date,
            CoverageEndDate  = null,
            IsMedicare       = false
        });

        if (!cobEntries.Any())
            return NotFound(new { MemberId = memberId, Message = "No COB entries found" });

        return Ok(cobEntries);
    }

    /// <summary>
    /// Create new coverage (typically from 834 transaction)
    /// </summary>
    /// <param name="request">Coverage creation request</param>
    [HttpPost]
    [ProducesResponseType(typeof(Coverage), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateCoverage([FromBody] CreateCoverageRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // TODO: Validate business rules
        // - Verify MemberId exists in Member Service
        // - Verify GroupNumber exists in Sponsor Service
        // - Verify PlanId exists in Benefit Plan Service
        // - Check for overlapping coverage periods

        var coverage = new Coverage
        {
            TenantId = TenantId,
            MemberId = request.MemberId,
            GroupNumber = request.GroupNumber,
            PlanId = request.PlanId,
            CoverageLevel = request.CoverageLevel,
            InsuranceLineCode = request.InsuranceLineCode,
            EffectiveDate = request.EffectiveDate,
            TerminationDate = request.TerminationDate,
            Status = request.EffectiveDate > DateTime.UtcNow.Date ? CoverageStatus.Pending : CoverageStatus.Active,
            IsCOBRA = request.IsCOBRA,
            COBRAEffectiveDate = request.COBRAEffectiveDate,
            MonthlyPremium = request.MonthlyPremium,
            EmployerContribution = request.EmployerContribution,
            MedicareCoverage = request.MedicareCoverage,
            OtherInsurance = request.OtherInsurance,
            MaintenanceTypeCode = request.MaintenanceTypeCode,
            MaintenanceReasonCode = request.MaintenanceReasonCode,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        // TODO: Save to Cosmos DB
        // await _coverageRepository.CreateAsync(coverage);

        return CreatedAtAction(nameof(GetCoverage), new { id = coverage.Id }, coverage);
    }

    /// <summary>
    /// Update coverage information
    /// </summary>
    /// <param name="id">Coverage ID</param>
    /// <param name="request">Update request</param>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Coverage), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateCoverage(
        [FromRoute] string id,
        [FromBody] UpdateCoverageRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // TODO: Fetch existing coverage
        var coverage = new Coverage { TenantId = TenantId, Id = id };

        // Update fields
        if (request.PlanId != null) coverage.PlanId = request.PlanId;
        if (request.CoverageLevel != null) coverage.CoverageLevel = request.CoverageLevel;
        if (request.Status.HasValue) coverage.Status = request.Status.Value;
        if (request.MonthlyPremium.HasValue) coverage.MonthlyPremium = request.MonthlyPremium.Value;
        if (request.EmployerContribution.HasValue) coverage.EmployerContribution = request.EmployerContribution.Value;
        if (request.MedicareCoverage != null) coverage.MedicareCoverage = request.MedicareCoverage;
        if (request.OtherInsurance != null) coverage.OtherInsurance = request.OtherInsurance;

        coverage.LastUpdatedDate = DateTime.UtcNow;
        coverage.LastUpdatedBy = User.Identity?.Name ?? "System";

        // TODO: Save to Cosmos DB
        // await _coverageRepository.UpdateAsync(coverage);

        return Ok(coverage);
    }

    /// <summary>
    /// Terminate coverage
    /// </summary>
    /// <param name="id">Coverage ID</param>
    /// <param name="terminationDate">Termination effective date</param>
    /// <param name="reasonCode">Termination reason code</param>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TerminateCoverage(
        [FromRoute] string id,
        [FromQuery] DateTime? terminationDate = null,
        [FromQuery] string? reasonCode = null)
    {
        // TODO: Update coverage status and termination date

        return NoContent();
    }

    /// <summary>
    /// Get coverage summary by sponsor group (for reporting)
    /// </summary>
    /// <param name="groupNumber">Sponsor group number</param>
    [HttpGet("group/{groupNumber}/summary")]
    [ProducesResponseType(typeof(GroupCoverageSummary), 200)]
    public async Task<IActionResult> GetGroupCoverageSummary([FromRoute] string groupNumber)
    {
        // TODO: Aggregate coverage data by group

        var summary = new GroupCoverageSummary
        {
            GroupNumber = groupNumber,
            TotalCovered = 370,
            ActiveCoverage = 365,
            PendingCoverage = 5,
            TerminatedCoverage = 12,
            ByPlan = new Dictionary<string, int>
            {
                { "PLAN-GOLD-HMO-001", 150 },
                { "PLAN-SILVER-PPO-001", 215 }
            },
            ByCoverageLevel = new Dictionary<string, int>
            {
                { CoverageLevelCodes.EmployeeOnly, 80 },
                { CoverageLevelCodes.Family, 285 }
            },
            TotalMonthlyPremium = 182500.00m
        };

        return Ok(summary);
    }
}

#region Request/Response Models

public class CreateCoverageRequest
{
    [Required]
    public string MemberId { get; set; } = string.Empty;

    [Required]
    public string GroupNumber { get; set; } = string.Empty;

    [Required]
    public string PlanId { get; set; } = string.Empty;

    public string? CoverageLevel { get; set; }
    public string? InsuranceLineCode { get; set; }

    [Required]
    public DateTime EffectiveDate { get; set; }

    public DateTime? TerminationDate { get; set; }
    public bool IsCOBRA { get; set; }
    public DateTime? COBRAEffectiveDate { get; set; }
    public decimal? MonthlyPremium { get; set; }
    public decimal? EmployerContribution { get; set; }
    public MedicareCoverageInfo? MedicareCoverage { get; set; }
    public OtherInsuranceInfo? OtherInsurance { get; set; }
    public string? MaintenanceTypeCode { get; set; }
    public string? MaintenanceReasonCode { get; set; }
}

public class UpdateCoverageRequest
{
    public string? PlanId { get; set; }
    public string? CoverageLevel { get; set; }
    public CoverageStatus? Status { get; set; }
    public decimal? MonthlyPremium { get; set; }
    public decimal? EmployerContribution { get; set; }
    public MedicareCoverageInfo? MedicareCoverage { get; set; }
    public OtherInsuranceInfo? OtherInsurance { get; set; }
}

public class CoverageListResponse
{
    public List<Coverage> Coverage { get; set; } = new();
    public string? ContinuationToken { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// A single COB (other insurance) entry for a member.
/// Returned by GET /member/{id}/cob.
/// CoverageSequence mirrors X12 SBR01: P = Primary, S = Secondary, T = Tertiary.
/// </summary>
public class CobEntryResponse
{
    public string PayerName { get; set; } = string.Empty;
    public string PayerId { get; set; } = string.Empty;
    /// <summary>P = Primary, S = Secondary, T = Tertiary.</summary>
    public string CoverageSequence { get; set; } = "S";
    public string? GroupNumber { get; set; }
    public string? PolicyNumber { get; set; }
    public DateTime CoverageBeginDate { get; set; }
    public DateTime? CoverageEndDate { get; set; }
    public bool IsMedicare { get; set; }
}

public class GroupCoverageSummary
{
    public string GroupNumber { get; set; } = string.Empty;
    public int TotalCovered { get; set; }
    public int ActiveCoverage { get; set; }
    public int PendingCoverage { get; set; }
    public int TerminatedCoverage { get; set; }
    public Dictionary<string, int> ByPlan { get; set; } = new();
    public Dictionary<string, int> ByCoverageLevel { get; set; } = new();
    public decimal TotalMonthlyPremium { get; set; }
}

#endregion
