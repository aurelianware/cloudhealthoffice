using Microsoft.AspNetCore.Mvc;
using CoverageService.Middleware;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
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
    private readonly ICoverageRepository _coverageRepository;
    private readonly IPcpAssignmentService? _pcpService;
    private readonly ICareTeamProjector? _careTeamProjector;
    private readonly ILogger<CoverageController> _logger;

    // Tenant context from middleware
    private string TenantId => HttpContext.GetTenantId();

    public CoverageController(
        ICoverageRepository coverageRepository,
        ILogger<CoverageController> logger,
        IPcpAssignmentService? pcpService = null,
        ICareTeamProjector? careTeamProjector = null)
    {
        _coverageRepository = coverageRepository;
        _pcpService = pcpService;
        _careTeamProjector = careTeamProjector;
        _logger = logger;
    }

    /// <summary>
    /// Search coverage records by various criteria
    /// </summary>
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
        _logger.LogInformation(
            "Searching coverage: member={Member}, group={Group}, plan={Plan}, activeOnly={ActiveOnly}",
            SanitizeForLog(memberId), SanitizeForLog(groupNumber), SanitizeForLog(planId), activeOnly);

        var (items, token) = await _coverageRepository.SearchAsync(
            TenantId, memberId, groupNumber, planId, activeOnly, pageSize, continuationToken);

        var coverageList = items.ToList();

        return Ok(new CoverageListResponse
        {
            Coverage = coverageList,
            ContinuationToken = token,
            TotalCount = coverageList.Count
        });
    }

    /// <summary>
    /// Get coverage details by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Coverage), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetCoverage([FromRoute] string id)
    {
        _logger.LogInformation("Fetching coverage by ID: {Id}", SanitizeForLog(id));

        var coverage = await _coverageRepository.GetByIdAsync(TenantId, id);
        if (coverage == null)
        {
            return NotFound(new { Id = id, Message = "Coverage not found" });
        }

        return Ok(coverage);
    }

    /// <summary>
    /// Get active coverage for a member (critical for eligibility checks)
    /// </summary>
    [HttpGet("member/{memberId}/active")]
    [ProducesResponseType(typeof(List<Coverage>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetActiveCoverage(
        [FromRoute] string memberId,
        [FromQuery] DateTime? serviceDate = null,
        [FromQuery] string? insuranceLineCode = null)
    {
        var checkDate = serviceDate ?? DateTime.UtcNow.Date;

        _logger.LogInformation(
            "Fetching active coverage for member {MemberId} on {ServiceDate}",
            SanitizeForLog(memberId), checkDate);

        var activeCoverage = await _coverageRepository.GetActiveCoverageByMemberIdAsync(
            TenantId, memberId, checkDate, insuranceLineCode);

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
    [HttpGet("member/{memberId}/history")]
    [ProducesResponseType(typeof(List<Coverage>), 200)]
    public async Task<IActionResult> GetCoverageHistory(
        [FromRoute] string memberId,
        [FromQuery] bool includeTerminated = true)
    {
        _logger.LogInformation("Fetching coverage history for member {MemberId}", SanitizeForLog(memberId));

        var history = await _coverageRepository.GetCoverageHistoryAsync(
            TenantId, memberId, includeTerminated);

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
    /// </summary>
    [HttpGet("member/{memberId}/cob")]
    [ProducesResponseType(typeof(List<CobEntryResponse>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetCobEntries(
        [FromRoute] string memberId,
        [FromQuery] DateTime? asOfDate = null)
    {
        var checkDate = asOfDate ?? DateTime.UtcNow;

        _logger.LogInformation("Fetching COB entries for member {MemberId}", SanitizeForLog(memberId));

        // Get all active coverage for the member, then extract COB info
        var coverages = await _coverageRepository.GetActiveCoverageByMemberIdAsync(
            TenantId, memberId, checkDate);

        var cobEntries = new List<CobEntryResponse>();

        foreach (var coverage in coverages)
        {
            if (coverage.OtherInsurance != null)
            {
                cobEntries.Add(new CobEntryResponse
                {
                    PayerName = coverage.OtherInsurance.PayerName ?? "Unknown Payer",
                    PayerId = coverage.OtherInsurance.PolicyNumber ?? "",
                    CoverageSequence = coverage.OtherInsurance.IsPrimaryPayer ? "P" : "S",
                    GroupNumber = coverage.OtherInsurance.GroupNumber,
                    CoverageBeginDate = coverage.OtherInsurance.EffectiveDate ?? coverage.EffectiveDate,
                    CoverageEndDate = null,
                    IsMedicare = false
                });
            }

            if (coverage.MedicareCoverage != null)
            {
                cobEntries.Add(new CobEntryResponse
                {
                    PayerName = "Medicare",
                    PayerId = coverage.MedicareCoverage.MedicareBeneficiaryId ?? "MEDICARE",
                    CoverageSequence = coverage.MedicareCoverage.IsPrimaryPayer ? "P" : "S",
                    CoverageBeginDate = coverage.MedicareCoverage.PartAEffectiveDate ?? coverage.EffectiveDate,
                    IsMedicare = true
                });
            }
        }

        if (!cobEntries.Any())
            return NotFound(new { MemberId = memberId, Message = "No COB entries found" });

        return Ok(cobEntries);
    }

    /// <summary>
    /// Create new coverage (typically from 834 transaction)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Coverage), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateCoverage([FromBody] CreateCoverageRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

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

        var created = await _coverageRepository.CreateAsync(coverage);
        return CreatedAtAction(nameof(GetCoverage), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update coverage information
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(Coverage), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateCoverage(
        [FromRoute] string id,
        [FromBody] UpdateCoverageRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var coverage = await _coverageRepository.GetByIdAsync(TenantId, id);
        if (coverage == null)
        {
            return NotFound(new { Id = id, Message = "Coverage not found" });
        }

        if (request.PlanId != null) coverage.PlanId = request.PlanId;
        if (request.CoverageLevel != null) coverage.CoverageLevel = request.CoverageLevel;
        if (request.Status.HasValue) coverage.Status = request.Status.Value;
        if (request.MonthlyPremium.HasValue) coverage.MonthlyPremium = request.MonthlyPremium.Value;
        if (request.EmployerContribution.HasValue) coverage.EmployerContribution = request.EmployerContribution.Value;
        if (request.MedicareCoverage != null) coverage.MedicareCoverage = request.MedicareCoverage;
        if (request.OtherInsurance != null) coverage.OtherInsurance = request.OtherInsurance;

        coverage.LastUpdatedDate = DateTime.UtcNow;
        coverage.LastUpdatedBy = User.Identity?.Name ?? "System";

        var updated = await _coverageRepository.UpdateAsync(coverage);
        return Ok(updated);
    }

    /// <summary>
    /// Terminate coverage
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TerminateCoverage(
        [FromRoute] string id,
        [FromQuery] DateTime? terminationDate = null,
        [FromQuery] string? reasonCode = null)
    {
        var coverage = await _coverageRepository.GetByIdAsync(TenantId, id);
        if (coverage == null)
        {
            return NotFound(new { Id = id, Message = "Coverage not found" });
        }

        coverage.Status = CoverageStatus.Terminated;
        coverage.TerminationDate = terminationDate ?? DateTime.UtcNow.Date;
        coverage.MaintenanceReasonCode = reasonCode;
        coverage.LastUpdatedDate = DateTime.UtcNow;
        coverage.LastUpdatedBy = User.Identity?.Name ?? "System";

        await _coverageRepository.UpdateAsync(coverage);
        return NoContent();
    }

    /// <summary>
    /// Get coverage summary by sponsor group (for reporting)
    /// </summary>
    [HttpGet("group/{groupNumber}/summary")]
    [ProducesResponseType(typeof(GroupCoverageSummary), 200)]
    public async Task<IActionResult> GetGroupCoverageSummary([FromRoute] string groupNumber)
    {
        _logger.LogInformation("Fetching coverage summary for group {GroupNumber}", SanitizeForLog(groupNumber));

        var coverages = await _coverageRepository.GetByGroupNumberAsync(TenantId, groupNumber);

        var summary = new GroupCoverageSummary
        {
            GroupNumber = groupNumber,
            TotalCovered = coverages.Count,
            ActiveCoverage = coverages.Count(c => c.Status == CoverageStatus.Active),
            PendingCoverage = coverages.Count(c => c.Status == CoverageStatus.Pending),
            TerminatedCoverage = coverages.Count(c => c.Status == CoverageStatus.Terminated),
            ByPlan = coverages.GroupBy(c => c.PlanId).ToDictionary(g => g.Key, g => g.Count()),
            ByCoverageLevel = coverages
                .Where(c => c.CoverageLevel != null)
                .GroupBy(c => c.CoverageLevel!)
                .ToDictionary(g => g.Key, g => g.Count()),
            TotalMonthlyPremium = coverages.Sum(c => c.MonthlyPremium ?? 0)
        };

        return Ok(summary);
    }

    /// <summary>
    /// Get coverage records assigned to a specific PCP by NPI.
    /// Used by the capitation-service to build member panel rosters.
    /// </summary>
    [HttpGet("by-pcp/{npi}")]
    [ProducesResponseType(typeof(List<Coverage>), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> GetCoverageByPcp(
        [FromRoute][StringLength(10, MinimumLength = 10)] string npi,
        [FromQuery] CoverageStatus? status = CoverageStatus.Active,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        if (npi.Length != 10 || !npi.All(char.IsDigit))
        {
            return BadRequest(new { Message = "NPI must be exactly 10 digits" });
        }

        _logger.LogInformation(
            "Fetching coverage by PCP NPI {Npi} with status {Status} and LOB {Lob}",
            SanitizeForLog(npi), status, lineOfBusiness);

        var coverages = await _coverageRepository.GetByPcpNpiAsync(
            TenantId, npi, status, lineOfBusiness);

        return Ok(coverages);
    }

    // ── Member-scoped PCP and termination endpoints ──────────────────
    //
    // These are called by member-service via HttpCoverageServiceClient. Coverage
    // is the authoritative store for PCP assignment; member-service proxies.

    /// <summary>
    /// Get the member's currently-assigned PCP derived from their active coverage.
    /// </summary>
    [HttpGet("member/{memberId}/pcp")]
    [ProducesResponseType(typeof(MemberPcpResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetMemberPcp(
        [FromRoute] string memberId,
        [FromQuery] DateTime? serviceDate = null)
    {
        _logger.LogInformation("Fetching PCP for member {MemberId}", SanitizeForLog(memberId));

        var checkDate = serviceDate ?? DateTime.UtcNow.Date;
        var active = await _coverageRepository.GetActiveCoverageByMemberIdAsync(
            TenantId, memberId, checkDate);

        var withPcp = active.FirstOrDefault(c => !string.IsNullOrEmpty(c.PcpNpi));
        if (withPcp == null)
        {
            return NotFound(new
            {
                MemberId = memberId,
                Message = "No active coverage with PCP assignment found."
            });
        }

        return Ok(new MemberPcpResponse
        {
            ProviderId = withPcp.PcpNpi ?? string.Empty,
            ProviderName = withPcp.PcpName ?? string.Empty,
            NPI = withPcp.PcpNpi ?? string.Empty,
            AssignedDate = withPcp.PcpAssignmentDate ?? withPcp.EffectiveDate,
            NetworkStatus = "In-Network"
        });
    }

    /// <summary>
    /// Assign or change the member's PCP. Validation is performed by
    /// <see cref="IPcpAssignmentService"/> in a fail-fast ordered ladder; the
    /// first failure is returned as 400 with a structured
    /// <see cref="PcpValidationError"/> body so the portal can localize and
    /// pick a remediation flow off <c>code</c>.
    /// </summary>
    [HttpPut("member/{memberId}/pcp")]
    [ProducesResponseType(typeof(MemberPcpResponse), 200)]
    [ProducesResponseType(typeof(PcpValidationError), 400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> AssignMemberPcp(
        [FromRoute] string memberId,
        [FromBody] AssignPcpBody request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        if (_pcpService == null)
        {
            return StatusCode(503, new { Message = "PCP assignment service not configured." });
        }

        var npi = request.ProviderNpi ?? request.ProviderId ?? string.Empty;
        var cmd = new AssignPcpCommand
        {
            ProviderNpi = npi,
            ProviderId = request.ProviderId,
            EffectiveDate = request.EffectiveDate,
            Reason = request.Reason,
            Source = ParseSource(request.AssignmentSource),
            MemberDateOfBirth = request.MemberDateOfBirth,
            AssignedBy = User.Identity?.Name ?? "member-service"
        };

        var result = await _pcpService.AssignAsync(TenantId, memberId, cmd, ct);
        if (!result.IsSuccess)
        {
            var err = result.Error!;
            // NoActiveCoverage is the one preflight failure that's a 404 (the
            // member has no coverage to attach a PCP to). Everything else is a
            // 400 on the assignment request.
            if (err.Code == PcpValidationCodes.NoActiveCoverage)
                return NotFound(err);
            return BadRequest(err);
        }

        var assignment = result.Assignment!;
        return Ok(new MemberPcpResponse
        {
            ProviderId = assignment.ProviderId ?? assignment.ProviderNpi,
            ProviderName = assignment.ProviderName ?? string.Empty,
            NPI = assignment.ProviderNpi,
            AssignedDate = assignment.EffectiveDate,
            // Wire the display-shaped network status ("In-Network" / "Out-of-Network")
            // — NOT the raw snapshot ("Tier1") which lives on the history row. The
            // portal colors off this value and assignment is only allowed when the
            // provider has an active participation, so success implies In-Network.
            NetworkStatus = ToDisplayNetworkStatus(assignment.NetworkStatusAtAssignment)
        });
    }

    private static string ToDisplayNetworkStatus(string? snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot)) return "In-Network";
        if (snapshot.Equals("Out-of-Network", StringComparison.OrdinalIgnoreCase)
            || snapshot.Equals("OutOfNetwork", StringComparison.OrdinalIgnoreCase)
            || snapshot.Equals("OON", StringComparison.OrdinalIgnoreCase))
        {
            return "Out-of-Network";
        }
        // Tier1/Tier2/Tier3 + anything else from an active participation ⇒ In-Network
        return "In-Network";
    }

    /// <summary>
    /// Full PCP assignment history for a member, newest first. The currently-open
    /// row (EndDate = null) is what <c>GetMemberPcp</c> reflects; older rows are
    /// closed by EndDate when superseded.
    /// </summary>
    [HttpGet("member/{memberId}/pcp/history")]
    [ProducesResponseType(typeof(IReadOnlyList<PcpAssignmentHistoryResponse>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetMemberPcpHistory([FromRoute] string memberId, CancellationToken ct)
    {
        if (_pcpService == null) return StatusCode(503, new { Message = "PCP assignment service not configured." });
        var history = await _pcpService.GetHistoryAsync(TenantId, memberId, ct);
        return Ok(history.Select(PcpAssignmentHistoryResponse.From).ToList());
    }

    /// <summary>
    /// FHIR R4 CareTeam projection for a member. Today only the PCP role is
    /// populated; specialist / care-manager participants will be added by
    /// roadmap 5.7 Phase 2 work without changing the projector's contract.
    /// </summary>
    [HttpGet("member/{memberId}/care-team")]
    [Produces("application/fhir+json", "application/json")]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetMemberCareTeam([FromRoute] string memberId, CancellationToken ct)
    {
        if (_pcpService == null || _careTeamProjector == null)
            return StatusCode(503, new { Message = "Care team projector not configured." });

        var current = await _pcpService.GetCurrentAsync(TenantId, memberId, ct);

        // CareTeam.status depends on the underlying coverage's lifecycle, including
        // termination — so we read coverage HISTORY here (includes terminated rows)
        // rather than active-only, otherwise the "inactive" branch in
        // CareTeamProjector.MapStatus is unreachable for terminated members.
        var history = await _coverageRepository.GetCoverageHistoryAsync(TenantId, memberId, includeTerminated: true);
        var primaryCoverage = history
            .Where(c => c.InsuranceLineCode == InsuranceLineCodes.Health)
            .OrderByDescending(c => c.EffectiveDate)
            .FirstOrDefault()
            ?? history.OrderByDescending(c => c.EffectiveDate).FirstOrDefault();

        var members = current != null
            ? new[] { CareTeamMember.FromPcp(current) }
            : Array.Empty<CareTeamMember>();

        var resource = _careTeamProjector.Project(memberId, primaryCoverage, members);
        return new ContentResult
        {
            ContentType = "application/fhir+json",
            Content = resource.ToJsonString(),
            StatusCode = 200
        };
    }

    private static PcpAssignmentSource ParseSource(string? raw) => raw switch
    {
        "AutoAssigned" => PcpAssignmentSource.AutoAssigned,
        "AdminAssigned" => PcpAssignmentSource.AdminAssigned,
        _ => PcpAssignmentSource.MemberChoice
    };

    /// <summary>
    /// Terminate all active coverages for a member.
    /// </summary>
    [HttpPost("member/{memberId}/terminate")]
    [ProducesResponseType(typeof(TerminateMemberCoverageResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TerminateMemberCoverage(
        [FromRoute] string memberId,
        [FromBody] TerminateMemberCoverageBody request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var asOf = request.TerminationDate == default ? DateTime.UtcNow.Date : request.TerminationDate;
        var active = (await _coverageRepository.GetActiveCoverageByMemberIdAsync(
            TenantId, memberId, asOf)).ToList();

        if (active.Count == 0)
        {
            return NotFound(new
            {
                MemberId = memberId,
                Message = "No active coverage for member."
            });
        }

        foreach (var coverage in active)
        {
            coverage.Status = CoverageStatus.Terminated;
            coverage.TerminationDate = asOf;
            if (!string.IsNullOrEmpty(request.ReasonCode))
                coverage.MaintenanceReasonCode = request.ReasonCode;
            coverage.LastUpdatedDate = DateTime.UtcNow;
            coverage.LastUpdatedBy = User.Identity?.Name ?? "member-service";
            await _coverageRepository.UpdateAsync(coverage);
        }

        return Ok(new TerminateMemberCoverageResponse
        {
            MemberId = memberId,
            TerminatedCount = active.Count,
            TerminationDate = asOf,
            ReasonCode = request.ReasonCode
        });
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public class AssignPcpBody
{
    public string? ProviderId { get; set; }
    public string? ProviderNpi { get; set; }
    public string? ProviderName { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Origin of the assignment. Accepted values: <c>MemberChoice</c> (default),
    /// <c>AutoAssigned</c>, <c>AdminAssigned</c>. See <see cref="PcpAssignmentSource"/>.
    /// </summary>
    public string? AssignmentSource { get; set; }

    /// <summary>
    /// Optional member DOB so the assignment service can enforce age-range
    /// participations (pediatric vs adult). When omitted the AGE_OUT_OF_RANGE
    /// check is skipped — caller is responsible for enforcing if needed.
    /// </summary>
    public DateTime? MemberDateOfBirth { get; set; }
}

public class MemberPcpResponse
{
    public string ProviderId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string NPI { get; set; } = string.Empty;
    public string Specialty { get; set; } = string.Empty;
    public string NetworkStatus { get; set; } = string.Empty;
    public DateTime AssignedDate { get; set; }
    public string? PracticeName { get; set; }
    public string? Phone { get; set; }
}

/// <summary>
/// Wire-shaped projection of <see cref="PcpAssignment"/> for the history endpoint.
/// Keeping a dedicated DTO (instead of returning the model directly) pins the
/// on-wire shape: every enum is a string, internal fields (tenantId) are dropped,
/// and downstream clients can rely on this contract across coverage-service
/// refactors.
/// </summary>
public class PcpAssignmentHistoryResponse
{
    public string Id { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public string ProviderNpi { get; set; } = string.Empty;
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public DateTime EffectiveDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? AssignmentReason { get; set; }
    public string AssignmentSource { get; set; } = "MemberChoice";
    public string NetworkStatusAtAssignment { get; set; } = "Unknown";
    public string? AssignedBy { get; set; }
    public DateTime CreatedDate { get; set; }

    public static PcpAssignmentHistoryResponse From(PcpAssignment a) => new()
    {
        Id = a.Id,
        MemberId = a.MemberId,
        CoverageId = a.CoverageId,
        ProviderNpi = a.ProviderNpi,
        ProviderId = a.ProviderId,
        ProviderName = a.ProviderName,
        EffectiveDate = a.EffectiveDate,
        EndDate = a.EndDate,
        AssignmentReason = a.AssignmentReason,
        AssignmentSource = a.AssignmentSource.ToString(),
        NetworkStatusAtAssignment = a.NetworkStatusAtAssignment,
        AssignedBy = a.AssignedBy,
        CreatedDate = a.CreatedDate
    };
}

public class TerminateMemberCoverageBody
{
    public DateTime TerminationDate { get; set; }
    public string? ReasonCode { get; set; }
    public string? Notes { get; set; }
}

public class TerminateMemberCoverageResponse
{
    public string MemberId { get; set; } = string.Empty;
    public int TerminatedCount { get; set; }
    public DateTime TerminationDate { get; set; }
    public string? ReasonCode { get; set; }
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
