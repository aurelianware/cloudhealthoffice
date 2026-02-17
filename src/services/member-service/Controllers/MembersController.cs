using Microsoft.AspNetCore.Mvc;
using MemberService.Middleware;
using MemberService.Models;
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

    // TODO: Replace with actual repository/service injection
    // private readonly IMemberRepository _memberRepository;
    // public MembersController(IMemberRepository memberRepository)
    // {
    //     _memberRepository = memberRepository;
    // }

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
        // TODO: Implement with Cosmos DB query
        // Build dynamic query based on filters

        // Mock response
        var mockMembers = new List<Member>
        {
            new Member
            {
                TenantId = TenantId,
                Id = "mem-001",
                MemberId = "MEM123456789",
                GroupNumber = "GRP-12345",
                IsSubscriber = true,
                FirstName = "John",
                LastName = "Doe",
                DateOfBirth = new DateTime(1980, 5, 15),
                Gender = "M",
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                Status = EnrollmentStatus.Active,
                RelationshipCode = RelationshipCodes.Self
            },
            new Member
            {
                TenantId = TenantId,
                Id = "mem-002",
                MemberId = "MEM123456790",
                GroupNumber = "GRP-12345",
                IsSubscriber = false,
                SubscriberMemberId = "MEM123456789",
                FirstName = "Jane",
                LastName = "Doe",
                DateOfBirth = new DateTime(1982, 8, 22),
                Gender = "F",
                EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                Status = EnrollmentStatus.Active,
                RelationshipCode = RelationshipCodes.Spouse
            }
        };

        return Ok(new MemberListResponse
        {
            Members = mockMembers,
            ContinuationToken = null,
            TotalCount = 2
        });
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
        // TODO: Implement with Cosmos DB query
        // var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);

        var member = new Member
        {
            TenantId = TenantId,
            Id = "mem-001",
            MemberId = memberId,
            SSN = "***-**-1234",  // Masked for security
            GroupNumber = "GRP-12345",
            IsSubscriber = true,
            RelationshipCode = RelationshipCodes.Self,
            FirstName = "John",
            MiddleName = "Michael",
            LastName = "Doe",
            DateOfBirth = new DateTime(1980, 5, 15),
            Gender = "M",
            Address = "456 Oak Street",
            City = "Dallas",
            State = "TX",
            ZipCode = "75202",
            Phone = "214-555-0200",
            Email = "john.doe@email.com",
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            Status = EnrollmentStatus.Active,
            EmploymentStatus = Models.EmploymentStatus.FullTime,
            TobaccoUser = false,
            MaintenanceTypeCode = "021",  // Addition
            MaintenanceReasonCode = "33"   // Birth/New enrollment
        };

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
        // TODO: Query dependents where SubscriberMemberId = memberId

        var dependents = new List<Member>
        {
            new Member
            {
                TenantId = TenantId,
                MemberId = "MEM123456790",
                SubscriberMemberId = memberId,
                IsSubscriber = false,
                RelationshipCode = RelationshipCodes.Spouse,
                FirstName = "Jane",
                LastName = "Doe",
                DateOfBirth = new DateTime(1982, 8, 22),
                Gender = "F",
                Status = EnrollmentStatus.Active
            },
            new Member
            {
                TenantId = TenantId,
                MemberId = "MEM123456791",
                SubscriberMemberId = memberId,
                IsSubscriber = false,
                RelationshipCode = RelationshipCodes.Child,
                FirstName = "Emily",
                LastName = "Doe",
                DateOfBirth = new DateTime(2010, 3, 10),
                Gender = "F",
                Status = EnrollmentStatus.Active
            }
        };

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

#endregion
