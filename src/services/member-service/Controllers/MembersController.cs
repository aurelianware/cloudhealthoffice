using Microsoft.AspNetCore.Mvc;
using MemberService.Middleware;
using MemberService.Models;
using MemberService.Repositories;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace MemberService.Controllers;

/// <summary>
/// Member management API - manages health plan subscribers and dependents.
/// Data populated by X12 834 Enrollment transactions.
/// </summary>
[ApiController]
[Route("api/v1/members")]
public class MembersController : ControllerBase
{
    // Tenant context from middleware
    private string TenantId => HttpContext.GetTenantId();

    private readonly IMemberRepository _memberRepository;
    public MembersController(IMemberRepository memberRepository)
    {
        _memberRepository = memberRepository;
    }

    /// <summary>
    /// Search members by various criteria
    /// </summary>
    /// <param name="memberId">Filter by member ID</param>
    /// <param name="groupNumber">Filter by sponsor group number</param>
    /// <param name="subscriberId">Filter dependents by subscriber ID</param>
    /// <param name="lastName">Search by last name (partial match)</param>
    /// <param name="dateOfBirth">Filter by date of birth</param>
    /// <param name="activeOnly">Return only active members</param>
    /// <param name="subscribersOnly">Return only subscribers (exclude dependents)</param>
    /// <param name="pageSize">Page size (max 100)</param>
    /// <param name="continuationToken">Continuation token for pagination</param>
    [HttpGet]
    [ProducesResponseType(typeof(MemberListResponse), 200)]
    public async Task<IActionResult> SearchMembers(
        [FromQuery] string? memberId = null,
        [FromQuery] string? groupNumber = null,
        [FromQuery] string? subscriberId = null,
        [FromQuery] string? lastName = null,
        [FromQuery] DateTime? dateOfBirth = null,
        [FromQuery] bool activeOnly = false,
        [FromQuery] bool subscribersOnly = false,
        [FromQuery][Range(1, 100)] int pageSize = 20,
        [FromQuery] string? continuationToken = null)
    {
        // If memberId is provided, do a direct lookup
        if (!string.IsNullOrEmpty(memberId))
        {
            var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
            return Ok(new MemberListResponse
            {
                Members = member != null ? new List<Member> { member } : new List<Member>(),
                ContinuationToken = null,
                TotalCount = member != null ? 1 : 0
            });
        }

        var (items, token) = await _memberRepository.SearchAsync(
            TenantId, groupNumber, lastName, dateOfBirth,
            activeOnly, subscribersOnly, pageSize, continuationToken);

        return Ok(new MemberListResponse
        {
            Members = items.ToList(),
            ContinuationToken = token,
            TotalCount = items.Count()
        });
    }

    /// <summary>
    /// Search members by free-text query (portal autocomplete).
    /// Searches across memberId, lastName, and subscriberId.
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<Member>), 200)]
    public async Task<IActionResult> SearchByQuery([FromQuery] string? q = null)
    {
        if (string.IsNullOrWhiteSpace(q))
            return await SearchMembers(pageSize: 20);

        // Try memberId lookup first, then fall back to lastName search
        var byId = await _memberRepository.GetByMemberIdAsync(TenantId, q);
        if (byId != null)
            return Ok(new List<Member> { byId });

        return await SearchMembers(
            memberId: null, groupNumber: null, subscriberId: null,
            lastName: q, dateOfBirth: null, activeOnly: false,
            subscribersOnly: false, pageSize: 20, continuationToken: null);
    }

    /// <summary>
    /// Get member details by member ID
    /// </summary>
    /// <param name="memberId">Member ID (834 REF*0F)</param>
    [HttpGet("{memberId}")]
    [ProducesResponseType(typeof(Member), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetMember([FromRoute] string memberId)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null)
            return NotFound();

        return Ok(member);
    }

    /// <summary>
    /// Create a new member (typically from 834 transaction)
    /// </summary>
    /// <param name="request">Member creation request</param>
    [HttpPost]
    [ProducesResponseType(typeof(Member), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateMember([FromBody] CreateMemberRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // TODO: Validate business rules
        // - Check GroupNumber exists in Sponsor Service
        // - If dependent, validate SubscriberMemberId exists
        // - Check for duplicate MemberId

        var member = new Member
        {
            TenantId = TenantId,
            MemberId = request.MemberId,
            SSN = request.SSN,
            GroupNumber = request.GroupNumber,
            IsSubscriber = request.IsSubscriber,
            SubscriberMemberId = request.SubscriberMemberId,
            RelationshipCode = request.RelationshipCode,
            FirstName = request.FirstName,
            LastName = request.LastName,
            MiddleName = request.MiddleName,
            DateOfBirth = request.DateOfBirth,
            Gender = request.Gender,
            Address = request.Address,
            City = request.City,
            State = request.State,
            ZipCode = request.ZipCode,
            Phone = request.Phone,
            Email = request.Email,
            EffectiveDate = request.EffectiveDate,
            TerminationDate = request.TerminationDate,
            Status = EnrollmentStatus.Pending,
            MaintenanceTypeCode = request.MaintenanceTypeCode,
            MaintenanceReasonCode = request.MaintenanceReasonCode,
            EmploymentStatus = request.EmploymentStatus,
            TobaccoUser = request.TobaccoUser,
            IsStudent = request.IsStudent,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow,
            CreatedBy = User.Identity?.Name ?? "System"
        };

        // TODO: Save to Cosmos DB
        // await _memberRepository.CreateAsync(member);

        return CreatedAtAction(nameof(GetMember), new { memberId = member.MemberId }, member);
    }

    /// <summary>
    /// Update member information
    /// </summary>
    /// <param name="memberId">Member ID</param>
    /// <param name="request">Update request</param>
    [HttpPut("{memberId}")]
    [ProducesResponseType(typeof(Member), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateMember(
        [FromRoute] string memberId,
        [FromBody] UpdateMemberRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // TODO: Fetch existing member
        var member = new Member { TenantId = TenantId, MemberId = memberId };

        // Update fields
        if (request.Address != null) member.Address = request.Address;
        if (request.City != null) member.City = request.City;
        if (request.State != null) member.State = request.State;
        if (request.ZipCode != null) member.ZipCode = request.ZipCode;
        if (request.Phone != null) member.Phone = request.Phone;
        if (request.Email != null) member.Email = request.Email;
        if (request.Status.HasValue) member.Status = request.Status.Value;
        if (request.EmploymentStatus.HasValue) member.EmploymentStatus = request.EmploymentStatus.Value;

        member.LastUpdatedDate = DateTime.UtcNow;
        member.LastUpdatedBy = User.Identity?.Name ?? "System";

        // TODO: Save to Cosmos DB
        // await _memberRepository.UpdateAsync(member);

        return Ok(member);
    }

    /// <summary>
    /// Terminate member coverage
    /// </summary>
    /// <param name="memberId">Member ID</param>
    /// <param name="terminationDate">Termination effective date</param>
    /// <param name="reasonCode">Termination reason code</param>
    [HttpDelete("{memberId}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> TerminateMember(
        [FromRoute] string memberId,
        [FromQuery] DateTime? terminationDate = null,
        [FromQuery] string? reasonCode = null)
    {
        // TODO: Update member status
        // If subscriber, optionally terminate all dependents

        return NoContent();
    }

    /// <summary>
    /// Get all dependents for a subscriber
    /// </summary>
    /// <param name="memberId">Subscriber member ID</param>
    [HttpGet("{memberId}/dependents")]
    [ProducesResponseType(typeof(List<Member>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetDependents([FromRoute] string memberId)
    {
        var dependents = await _memberRepository.GetDependentsAsync(TenantId, memberId);
        return Ok(dependents);
    }

    /// <summary>
    /// Verify member eligibility (quick check for active coverage)
    /// </summary>
    /// <param name="memberId">Member ID</param>
    /// <param name="serviceDate">Service date to check (defaults to today)</param>
    [HttpGet("{memberId}/eligibility")]
    [ProducesResponseType(typeof(EligibilityCheckResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CheckEligibility(
        [FromRoute] string memberId,
        [FromQuery] DateTime? serviceDate = null)
    {
        var checkDate = serviceDate ?? DateTime.UtcNow.Date;

        // TODO: Query member and check coverage dates
        var isEligible = true;  // Mock
        var reason = "Active coverage";

        return Ok(new EligibilityCheckResponse
        {
            MemberId = memberId,
            ServiceDate = checkDate,
            IsEligible = isEligible,
            Reason = reason,
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            TerminationDate = null
        });
    }

    // ── Portal integration endpoints ─────────────────────────────────

    /// <summary>
    /// Get member's PCP assignment
    /// </summary>
    [HttpGet("{memberId}/pcp")]
    [ProducesResponseType(typeof(MemberPcpResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetMemberPcp([FromRoute] string memberId)
    {
        // TODO: Look up PCP assignment from coverage-service or member record
        return Ok(new MemberPcpResponse
        {
            ProviderId = "prov-001",
            ProviderName = "Dr. Sarah Chen, MD",
            NPI = "1234567890",
            Specialty = "Internal Medicine",
            NetworkStatus = "In-Network",
            AssignedDate = DateTime.UtcNow.AddMonths(-6),
            PracticeName = "Austin Primary Care Associates",
            Phone = "512-555-0100"
        });
    }

    /// <summary>
    /// Assign or change member's PCP
    /// </summary>
    [HttpPut("{memberId}/pcp")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> AssignPcp([FromRoute] string memberId, [FromBody] AssignPcpRequest request)
    {
        // TODO: Update PCP assignment in coverage-service
        return Ok(new { memberId, providerId = request.ProviderId, effectiveDate = request.EffectiveDate });
    }

    /// <summary>
    /// Get member's coverage history (enrollments, plan changes, terminations)
    /// </summary>
    [HttpGet("{memberId}/coverage-history")]
    [ProducesResponseType(typeof(List<CoverageHistoryEvent>), 200)]
    public async Task<IActionResult> GetCoverageHistory([FromRoute] string memberId)
    {
        // TODO: Query coverage-service for history
        return Ok(new List<CoverageHistoryEvent>
        {
            new() { EventDate = DateTime.UtcNow.AddMonths(-6), EventType = "Enrolled", Description = "Initial enrollment via 834", ChangedBy = "System" },
            new() { EventDate = DateTime.UtcNow.AddMonths(-3), EventType = "PcpChange", Description = "PCP changed to Dr. Chen", ChangedBy = "Member Portal" }
        });
    }

    /// <summary>
    /// Get member's 834 enrollment transaction history
    /// </summary>
    [HttpGet("{memberId}/834-transactions")]
    [ProducesResponseType(typeof(List<Enrollment834Record>), 200)]
    public async Task<IActionResult> Get834Transactions([FromRoute] string memberId)
    {
        // TODO: Query enrollment-import-service for 834 records
        return Ok(new List<Enrollment834Record>
        {
            new() { TransactionId = "TXN-001", BatchId = "BATCH-001", MemberId = memberId, MemberName = "Member",
                     MaintenanceTypeCode = "021", TransactionDate = DateTime.UtcNow.AddMonths(-6), Status = "Accepted" }
        });
    }

    /// <summary>
    /// Get member's accumulator balances (deductible, OOP, service limits)
    /// </summary>
    [HttpGet("{memberId}/accumulators")]
    [ProducesResponseType(typeof(MemberAccumulatorsResponse), 200)]
    public async Task<IActionResult> GetAccumulators([FromRoute] string memberId)
    {
        // TODO: Query accumulator service / claims-service for plan year totals
        return Ok(new MemberAccumulatorsResponse
        {
            IndividualDeductibleUsed = 750m, IndividualDeductibleLimit = 2000m,
            FamilyDeductibleUsed = 1500m, FamilyDeductibleLimit = 6000m,
            IndividualOopUsed = 1200m, IndividualOopLimit = 8150m,
            FamilyOopUsed = 2400m, FamilyOopLimit = 16300m
        });
    }

    /// <summary>
    /// Terminate member enrollment
    /// </summary>
    [HttpPost("{memberId}/terminate")]
    [ProducesResponseType(200)]
    public async Task<IActionResult> TerminateMember([FromRoute] string memberId, [FromBody] TerminateMemberRequest request)
    {
        // TODO: Process termination via coverage-service
        return Ok(new { memberId, terminationDate = request.TerminationDate, reasonCode = request.ReasonCode });
    }
}

#region Request/Response Models

public class CreateMemberRequest
{
    [Required]
    public string MemberId { get; set; } = string.Empty;

    public string? SSN { get; set; }

    [Required]
    public string GroupNumber { get; set; } = string.Empty;

    [Required]
    public bool IsSubscriber { get; set; }

    public string? SubscriberMemberId { get; set; }
    public string? RelationshipCode { get; set; }

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    public string? MiddleName { get; set; }

    [Required]
    public DateTime DateOfBirth { get; set; }

    public string? Gender { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    [Required]
    public DateTime EffectiveDate { get; set; }

    public DateTime? TerminationDate { get; set; }
    public string? MaintenanceTypeCode { get; set; }
    public string? MaintenanceReasonCode { get; set; }
    public EmploymentStatus? EmploymentStatus { get; set; }
    public bool? TobaccoUser { get; set; }
    public bool? IsStudent { get; set; }
}

public class UpdateMemberRequest
{
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public EnrollmentStatus? Status { get; set; }
    public EmploymentStatus? EmploymentStatus { get; set; }
}

public class MemberListResponse
{
    public List<Member> Members { get; set; } = new();
    public string? ContinuationToken { get; set; }
    public int TotalCount { get; set; }
}

public class EligibilityCheckResponse
{
    public string MemberId { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public bool IsEligible { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime? EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
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

public class CoverageHistoryEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString();
    public DateTime EventDate { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ChangedBy { get; set; }
}

public class Enrollment834Record
{
    public string TransactionId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MaintenanceTypeCode { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class MemberAccumulatorsResponse
{
    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal FamilyDeductibleUsed { get; set; }
    public decimal FamilyDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public decimal FamilyOopUsed { get; set; }
    public decimal FamilyOopLimit { get; set; }
    public List<object> ServiceAccumulators { get; set; } = new();
    public List<object> RecentActivity { get; set; } = new();
}

public class AssignPcpRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }
}

public class TerminateMemberRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public DateTime TerminationDate { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

#endregion
