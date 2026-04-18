using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MemberService.Middleware;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MemberService.Controllers;

/// <summary>
/// Family relationships for a member. Graduates the legacy
/// <c>Member.SubscriberMemberId</c> FK model to a symmetric
/// <see cref="FamilyRelationship"/> graph.
/// </summary>
[ApiController]
[Route("api/v1/members/{memberId}/relationships")]
public class FamilyRelationshipsController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IFamilyRelationshipService _service;
    private readonly IMemberRepository _memberRepo;
    private readonly IMemberEventPublisher _eventPublisher;
    private readonly IIdentifierEncryptor _encryptor;

    public FamilyRelationshipsController(
        IFamilyRelationshipService service,
        IMemberRepository memberRepo,
        IMemberEventPublisher eventPublisher,
        IIdentifierEncryptor encryptor)
    {
        _service = service;
        _memberRepo = memberRepo;
        _eventPublisher = eventPublisher;
        _encryptor = encryptor;
    }

    // ── Read ────────────────────────────────────────────────────────────

    /// <summary>List active relationships touching <paramref name="memberId"/>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(FamilyRelationshipListResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> List(
        [FromRoute] string memberId,
        [FromQuery] bool includeDeleted = false,
        CancellationToken ct = default)
    {
        var member = await _memberRepo.GetByMemberIdAsync(TenantId, memberId);
        // Hide drafts from the relationships endpoint the same way MembersController.GetMember
        // hides them — a draft Member's edges shouldn't be enumerable until the wizard commits.
        if (member == null || member.IsDraft) return NotFound();

        var rows = await _service.ListForMemberAsync(TenantId, memberId, includeDeleted, ct);

        // Only surface the edges where this member is the subject — each pair has two
        // rows and we don't want to return them twice.
        var mine = rows.Where(r => r.SubjectMemberId == memberId).ToList();
        return Ok(new FamilyRelationshipListResponse
        {
            MemberId = memberId,
            Relationships = mine,
            TotalCount = mine.Count,
        });
    }

    [HttpGet("{relId}")]
    [ProducesResponseType(typeof(FamilyRelationship), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Get([FromRoute] string memberId, [FromRoute] string relId, CancellationToken ct)
    {
        var row = await _service.GetByIdAsync(TenantId, relId, ct);
        if (row == null || (row.SubjectMemberId != memberId && row.RelatedMemberId != memberId))
            return NotFound();
        return Ok(row);
    }

    // ── Create / update / end ────────────────────────────────────────────

    [HttpPost]
    [ProducesResponseType(typeof(FamilyRelationship), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Create(
        [FromRoute] string memberId,
        [FromBody] CreateFamilyRelationshipRequest request,
        CancellationToken ct)
    {
        if (request == null) return BadRequest("Body is required.");
        // Force the URL's memberId to be the subject; ignore any mismatched body value
        // to avoid confusing URL vs body ownership.
        request.SubjectMemberId = memberId;
        try
        {
            var created = await _service.CreateAsync(TenantId, request, User.Identity?.Name, ct);
            return CreatedAtAction(nameof(Get), new { memberId, relId = created.Id }, created);
        }
        catch (FamilyRelationshipValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{relId}")]
    [ProducesResponseType(typeof(FamilyRelationship), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Update(
        [FromRoute] string memberId,
        [FromRoute] string relId,
        [FromBody] UpdateFamilyRelationshipRequest request,
        CancellationToken ct)
    {
        var row = await _service.GetByIdAsync(TenantId, relId, ct);
        if (row == null || (row.SubjectMemberId != memberId && row.RelatedMemberId != memberId))
            return NotFound();
        try
        {
            var updated = await _service.UpdateAsync(TenantId, relId, request, User.Identity?.Name, ct);
            return Ok(updated);
        }
        catch (FamilyRelationshipValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>End a relationship (normal wind-down).</summary>
    [HttpPost("{relId}/end")]
    [ProducesResponseType(typeof(FamilyRelationship), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> End(
        [FromRoute] string memberId,
        [FromRoute] string relId,
        [FromBody] EndRelationshipRequest? request,
        CancellationToken ct)
    {
        var row = await _service.GetByIdAsync(TenantId, relId, ct);
        if (row == null || (row.SubjectMemberId != memberId && row.RelatedMemberId != memberId))
            return NotFound();
        try
        {
            var endDate = request?.EndDate ?? DateTime.UtcNow;
            var updated = await _service.EndAsync(TenantId, relId, endDate, User.Identity?.Name, ct);
            return Ok(updated);
        }
        catch (FamilyRelationshipValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Soft-delete (data-entry error correction within 24h). Hard delete is
    /// intentionally not exposed — the relationship may be referenced by downstream
    /// claims, authorizations, and audit records.
    ///
    /// Authorization: this endpoint is intended to be gated to admin actors at the
    /// API gateway / ingress (no controller-level policy is defined because the
    /// service stack does not configure <c>AddAuthorization</c> here; policies live
    /// at the gateway). The 24h creation-window guard inside
    /// <c>FamilyRelationshipService.SoftDeleteAsync</c> is defense-in-depth but not
    /// a substitute for the gateway policy.
    /// </summary>
    [HttpDelete("{relId}")]
    [ProducesResponseType(typeof(FamilyRelationship), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> SoftDelete(
        [FromRoute] string memberId,
        [FromRoute] string relId,
        [FromQuery] string reason,
        CancellationToken ct)
    {
        var row = await _service.GetByIdAsync(TenantId, relId, ct);
        if (row == null || (row.SubjectMemberId != memberId && row.RelatedMemberId != memberId))
            return NotFound();
        try
        {
            var updated = await _service.SoftDeleteAsync(TenantId, relId, reason, User.Identity?.Name, ct: ct);
            return Ok(updated);
        }
        catch (FamilyRelationshipValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Add-Dependent wizard ────────────────────────────────────────────

    /// <summary>
    /// Two-step (but externally atomic-feeling) Add Dependent: creates a new Member in
    /// <c>IsDraft=true</c> state, then creates the symmetric relationship pair, then
    /// promotes the draft (<c>IsDraft=false</c>).
    ///
    /// If the relationship create fails, the Member is left as a draft — drafts are
    /// invisible to normal queries and a reconciler sweeps them after their TTL.
    /// (Outbox-pattern dispatch will replace this once the member-event consumer
    /// pipeline is in place; see architectural notes in docs/migrations/...)
    /// </summary>
    [HttpPost("/api/v1/members/{memberId}/dependents")]
    [ProducesResponseType(typeof(AddDependentResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> AddDependent(
        [FromRoute] string memberId,
        [FromBody] AddDependentRequest request,
        CancellationToken ct)
    {
        if (request == null || request.Member == null || request.Relationship == null)
            return BadRequest("Both member and relationship payloads are required.");

        var subscriber = await _memberRepo.GetByMemberIdAsync(TenantId, memberId);
        if (subscriber == null) return NotFound($"Subscriber '{memberId}' not found.");

        // Fail fast on an invalid relationship code before touching the Member store.
        if (!FamilyRelationshipCodes.IsValid(request.Relationship.RelationshipCode))
            return BadRequest(new { error = $"Unknown relationshipCode '{request.Relationship.RelationshipCode}'." });

        var existing = await _memberRepo.GetByMemberIdAsync(TenantId, request.Member.MemberId);
        if (existing != null)
            return Conflict(new { memberId = request.Member.MemberId, message = "MemberId already exists in this tenant." });

        var identifiers = new List<MemberIdentifier>();
        if (!string.IsNullOrEmpty(request.Member.SSN))
        {
            var cipher = await _encryptor.EncryptAsync(request.Member.SSN, ct);
            identifiers.Add(new MemberIdentifier
            {
                Type = MemberIdentifierType.SSN,
                System = FhirIdentifierSystems.SSN,
                Value = cipher ?? string.Empty,
                IsEncrypted = _encryptor.IsEnabled,
            });
        }

        var dependent = new Member
        {
            TenantId = TenantId,
            MemberId = request.Member.MemberId,
            SSN = _encryptor.IsEnabled ? null : request.Member.SSN,
            GroupNumber = request.Member.GroupNumber ?? subscriber.GroupNumber,
            IsSubscriber = false,
#pragma warning disable CS0618 // back-compat: derived on read after migration
            SubscriberMemberId = memberId,
            RelationshipCode = request.Relationship.RelationshipCode,
#pragma warning restore CS0618
            FirstName = request.Member.FirstName,
            LastName = request.Member.LastName,
            MiddleName = request.Member.MiddleName,
            DateOfBirth = request.Member.DateOfBirth,
            Gender = request.Member.Gender,
            Address = request.Member.Address ?? subscriber.Address,
            City = request.Member.City ?? subscriber.City,
            State = request.Member.State ?? subscriber.State,
            ZipCode = request.Member.ZipCode ?? subscriber.ZipCode,
            Phone = request.Member.Phone,
            Email = request.Member.Email,
            EffectiveDate = request.Member.EffectiveDate,
            TerminationDate = request.Member.TerminationDate,
            Status = EnrollmentStatus.Pending,
            LineOfBusiness = subscriber.LineOfBusiness,
            Identifiers = identifiers,
            IsDraft = true,
            CreatedBy = User.Identity?.Name ?? "portal",
        };

        await _memberRepo.CreateAsync(dependent);

        FamilyRelationship createdRelationship;
        try
        {
            createdRelationship = await _service.CreateAsync(TenantId, new CreateFamilyRelationshipRequest
            {
                SubjectMemberId = dependent.MemberId,
                RelatedMemberId = memberId,
                RelationshipCode = request.Relationship.RelationshipCode,
                StartDate = request.Relationship.StartDate == default
                    ? request.Member.EffectiveDate
                    : request.Relationship.StartDate,
                EndDate = request.Relationship.EndDate,
                IsCustodial = request.Relationship.IsCustodial,
                QmcsoReference = request.Relationship.QmcsoReference,
            }, User.Identity?.Name, ct);
        }
        catch (FamilyRelationshipValidationException ex)
        {
            // Leave the Member as a draft — the reconciler will purge it after TTL.
            // Returning 400 lets the portal surface a specific error to the user.
            return BadRequest(new { error = ex.Message, memberId = dependent.MemberId, draft = true });
        }

        dependent.IsDraft = false;
        dependent.LastUpdatedDate = DateTime.UtcNow;
        await _memberRepo.UpdateAsync(dependent);

        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = dependent.MemberId,
            EventId = request.EventId ?? Guid.NewGuid().ToString(),
            EventType = MemberEventType.MemberCreated,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["source"] = "AddDependent",
                ["memberId"] = dependent.MemberId,
                ["subscriberMemberId"] = memberId,
                ["relationshipCode"] = request.Relationship.RelationshipCode,
                ["relationshipId"] = createdRelationship.Id,
            },
        }, ct);

        // Location points at the created relationship row, not the Member. A GET on
        // api/v1/members/{depMemberId}/relationships/{relId} will resolve it.
        return CreatedAtAction(nameof(Get),
            new { memberId = dependent.MemberId, relId = createdRelationship.Id },
            new AddDependentResponse
            {
                Member = dependent,
                SubscriberMemberId = memberId,
                Relationship = createdRelationship,
            });
    }
}

#region Request / response models

public class FamilyRelationshipListResponse
{
    public string MemberId { get; set; } = string.Empty;
    public List<FamilyRelationship> Relationships { get; set; } = new();
    public int TotalCount { get; set; }
}

public class EndRelationshipRequest
{
    public DateTime? EndDate { get; set; }
}

public class AddDependentRequest
{
    [Required]
    public AddDependentMember Member { get; set; } = new();

    [Required]
    public AddDependentRelationship Relationship { get; set; } = new();

    public string? EventId { get; set; }
}

public class AddDependentMember
{
    [Required] public string MemberId { get; set; } = string.Empty;
    [Required] public string FirstName { get; set; } = string.Empty;
    [Required] public string LastName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    [Required] public DateTime DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? SSN { get; set; }
    public string? GroupNumber { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    [Required] public DateTime EffectiveDate { get; set; }
    public DateTime? TerminationDate { get; set; }
}

public class AddDependentRelationship
{
    [Required] public string RelationshipCode { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsCustodial { get; set; }
    public string? QmcsoReference { get; set; }
}

public class AddDependentResponse
{
    public Member Member { get; set; } = new();
    public string SubscriberMemberId { get; set; } = string.Empty;
    public FamilyRelationship? Relationship { get; set; }
}

#endregion
