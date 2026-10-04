using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using PersonalRepresentativeService.Middleware;
using PersonalRepresentativeService.Models;
using PersonalRepresentativeService.Repositories;
using PersonalRepresentativeService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

// TODO(review-workflow-followup): multi-step approval workflows (legal
// review pending, awaiting notary verification) are not modeled in this
// controller. When a review workflow lands, it joins between Draft and
// Active.
// TODO(appeals-modernization-followup): appeals-service will consume this
// controller's resolver endpoint (in MemberRepresentativesController) when
// it modernizes.

namespace PersonalRepresentativeService.Controllers;

/// <summary>
/// Personal Representative delegation records (§164.502(g)). Representatives
/// are never hard-deleted; the lifecycle is Draft → Active → Inactive, with
/// an append-only audit trail on every transition. Associations to members
/// are symmetric pairs written atomically.
///
/// Identity: the tenant and the acting user come from the validated CHO
/// token only (<see cref="ICurrentActor"/>). Who established, activated,
/// associated, removed or revoked a representative is the token subject,
/// never a body field. Reads need <c>members:read</c>, writes
/// <c>members:write</c> (defaults set in Program.cs).
/// </summary>
[ApiController]
[Route("api/v1/personal-representatives")]
public class PersonalRepresentativesController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    /// <summary>The token subject. Every write records this as its actor.</summary>
    private string Actor => _currentActor.UserId;

    private readonly IPersonalRepRepository _reps;
    private readonly IPersonalRepEventRepository _events;
    private readonly IPersonalRepFieldEncryptor _encryptor;
    private readonly IPersonalRepEventPublisher _publisher;
    private readonly ICurrentActor _currentActor;
    private readonly IPersonalRepActivationControls _activationControls;
    private readonly ILogger<PersonalRepresentativesController>? _logger;

    public PersonalRepresentativesController(
        IPersonalRepRepository reps,
        IPersonalRepEventRepository events,
        IPersonalRepFieldEncryptor encryptor,
        IPersonalRepEventPublisher publisher,
        ICurrentActor currentActor,
        IPersonalRepActivationControls activationControls,
        ILogger<PersonalRepresentativesController>? logger = null)
    {
        _currentActor = currentActor;
        _activationControls = activationControls;
        _reps = reps;
        _events = events;
        _encryptor = encryptor;
        _publisher = publisher;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(PersonalRepresentative), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateRepresentative(
        [FromBody] CreatePersonalRepRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var actor = Actor;

        var rep = new PersonalRepresentative
        {
            TenantId = TenantId,
            CredentialType = request.CredentialType,
            Status = PersonalRepStatus.Draft,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            ExpiresAt = request.ExpiresAt,
            ProofOfAuthorityDocumentId = request.ProofOfAuthorityDocumentId,
            FirstName = await _encryptor.EncryptAsync(request.FirstName, ct),
            MiddleName = await _encryptor.EncryptAsync(request.MiddleName, ct),
            LastName = await _encryptor.EncryptAsync(request.LastName, ct),
            Email = await _encryptor.EncryptAsync(request.Email, ct),
            PhoneNumber = await _encryptor.EncryptAsync(request.PhoneNumber, ct),
            MailingAddressLine1 = await _encryptor.EncryptAsync(request.MailingAddressLine1, ct),
            MailingAddressLine2 = await _encryptor.EncryptAsync(request.MailingAddressLine2, ct),
            MailingAddressCity = await _encryptor.EncryptAsync(request.MailingAddressCity, ct),
            MailingAddressStateCode = await _encryptor.EncryptAsync(request.MailingAddressStateCode, ct),
            MailingAddressPostalCode = await _encryptor.EncryptAsync(request.MailingAddressPostalCode, ct),
            RelationshipNotes = await _encryptor.EncryptAsync(request.RelationshipNotes, ct),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = actor
        };

        var genesis = BuildRepEvent(rep, PersonalRepEventType.PersonalRepCreated,
            fromStatus: null, toStatus: PersonalRepStatus.Draft, actor, request.EventId, memberId: null);

        var created = await _reps.CreateAsync(rep, genesis);
        await _publisher.PublishStatusChangedAsync(
            created, fromStatus: null, toStatus: PersonalRepStatus.Draft,
            associatedMemberIds: Array.Empty<string>(),
            actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(created, ct);
        return CreatedAtAction(nameof(GetRepresentative),
            new { repId = created.Id }, view);
    }

    [HttpGet("{repId}")]
    [ProducesResponseType(typeof(PersonalRepresentative), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetRepresentative(
        [FromRoute] string repId,
        CancellationToken ct)
    {
        var rep = await _reps.GetByIdAsync(TenantId, repId, ct);
        if (rep == null) return NotFound();

        rep = await MaybeObserveExpiryAsync(rep, ct);

        var view = await DecryptForResponseAsync(rep, ct);
        return Ok(view);
    }

    [HttpGet("{repId}/history")]
    [ProducesResponseType(typeof(PersonalRepHistoryResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetHistory(
        [FromRoute] string repId,
        CancellationToken ct)
    {
        var rep = await _reps.GetByIdAsync(TenantId, repId, ct);
        if (rep == null) return NotFound();

        var events = await _events.ListByRepAsync(TenantId, repId, ct);
        return Ok(new PersonalRepHistoryResponse { Items = events.ToList() });
    }

    /// <summary>
    /// Draft → Active. Checked by <see cref="IPersonalRepActivationControls"/>
    /// before anything is written: a user (not a service) other than the
    /// creator activates (unless the tenant turned the second-person rule off),
    /// and guardians, healthcare powers of attorney and surrogates need a
    /// proof-of-authority document in member-document-service that belongs to
    /// this tenant and is linked to the representative's members. Who verified
    /// the document and when is stored on the representative and on the audit
    /// and Kafka events.
    /// </summary>
    [HttpPost("{repId}/activate")]
    [ProducesResponseType(typeof(PersonalRepresentative), 200)]
    [ProducesResponseType(typeof(ProblemDetails), 400)]
    [ProducesResponseType(typeof(ProblemDetails), 403)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(typeof(ProblemDetails), 422)]
    [ProducesResponseType(typeof(ProblemDetails), 503)]
    public async Task<IActionResult> Activate(
        [FromRoute] string repId,
        [FromBody] ActivatePersonalRepRequest? request,
        CancellationToken ct)
    {
        var rep = await _reps.GetByIdAsync(TenantId, repId, ct);
        if (rep == null) return NotFound();

        rep = await MaybeObserveExpiryAsync(rep, ct);

        if (rep.Status == PersonalRepStatus.Active)
        {
            var view = await DecryptForResponseAsync(rep, ct);
            return Ok(view);
        }

        try
        {
            PersonalRepStateMachine.EnsureAllowed(rep.Status, PersonalRepStatus.Active);
        }
        catch (InvalidPersonalRepTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        var memberIds = (await _reps.ListAssociationsForRepAsync(TenantId, repId, activeOnly: true, ct: ct))
            .Select(a => a.MemberId).Distinct().ToList();

        var decision = await _activationControls.EvaluateAsync(
            rep, request?.ProofOfAuthorityDocumentId, memberIds, ct);
        if (!decision.Allowed)
        {
            return StatusCode(decision.StatusCode, new ProblemDetails
            {
                Status = decision.StatusCode,
                Title = decision.Title,
                Detail = decision.Detail
            });
        }

        var actor = Actor;
        var from = rep.Status;
        var now = DateTime.UtcNow;
        rep.Status = PersonalRepStatus.Active;
        rep.ActivatedBy = actor;
        rep.ActivatedAt = now;
        if (!rep.EffectiveFrom.HasValue)
            rep.EffectiveFrom = rep.ActivatedAt;
        if (decision.VerifiedDocumentId != null)
        {
            rep.ProofOfAuthorityDocumentId = decision.VerifiedDocumentId;
            rep.ProofOfAuthorityVerifiedBy = actor;
            rep.ProofOfAuthorityVerifiedAt = now;
        }

        var auditEvent = BuildRepEvent(rep, PersonalRepEventType.PersonalRepActivated,
            fromStatus: from, toStatus: PersonalRepStatus.Active, actor, request?.EventId, memberId: null);
        auditEvent.Payload!["proofOfAuthorityDocumentId"] = rep.ProofOfAuthorityDocumentId;
        auditEvent.Payload["proofOfAuthorityVerifiedBy"] = rep.ProofOfAuthorityVerifiedBy;
        auditEvent.Payload["proofOfAuthorityVerifiedAt"] = rep.ProofOfAuthorityVerifiedAt?.ToString("o");
        auditEvent.Payload["createdBy"] = rep.CreatedBy;
        auditEvent.Payload["secondPersonOverride"] = decision.SecondPersonOverridden;

        PersonalRepresentative updated;
        try
        {
            updated = await _reps.TransitionStatusAsync(rep, auditEvent);
        }
        catch (InvalidPersonalRepTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        await _publisher.PublishStatusChangedAsync(
            updated, fromStatus: from, toStatus: PersonalRepStatus.Active,
            associatedMemberIds: memberIds,
            actor, HttpContext.TraceIdentifier, ct);

        var result = await DecryptForResponseAsync(updated, ct);
        return Ok(result);
    }

    [HttpPost("{repId}/revoke")]
    [ProducesResponseType(typeof(PersonalRepresentative), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> Revoke(
        [FromRoute] string repId,
        [FromBody] RevokePersonalRepRequest? request,
        CancellationToken ct)
    {
        // Only the system records a lapse (read-time expiry observation); a
        // caller claiming "Expired" would misstate why authority ended.
        if (request?.ReasonCode == PersonalRepInactivationReasonCode.Expired)
        {
            return BadRequest(new ProblemDetails
            {
                Status = 400,
                Title = "Invalid revocation reason",
                Detail = "ReasonCode Expired is recorded by the system when ExpiresAt passes; choose the reason the authority ended."
            });
        }

        var rep = await _reps.GetByIdAsync(TenantId, repId, ct);
        if (rep == null) return NotFound();

        rep = await MaybeObserveExpiryAsync(rep, ct);

        if (rep.Status == PersonalRepStatus.Inactive)
        {
            var view = await DecryptForResponseAsync(rep, ct);
            return Ok(view);
        }

        try
        {
            PersonalRepStateMachine.EnsureAllowed(rep.Status, PersonalRepStatus.Inactive);
        }
        catch (InvalidPersonalRepTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        var actor = Actor;
        var from = rep.Status;
        rep.Status = PersonalRepStatus.Inactive;
        rep.InactivatedBy = actor;
        rep.InactivatedAt = DateTime.UtcNow;
        rep.InactivationReasonCode = request?.ReasonCode ?? PersonalRepInactivationReasonCode.PoaRevoked;

        var auditEvent = BuildRepEvent(rep, PersonalRepEventType.PersonalRepInactivated,
            fromStatus: from, toStatus: PersonalRepStatus.Inactive, actor, request?.EventId, memberId: null);

        PersonalRepresentative updated;
        try
        {
            updated = await _reps.TransitionStatusAsync(rep, auditEvent);
        }
        catch (InvalidPersonalRepTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        var memberIds = (await _reps.ListAssociationsForRepAsync(TenantId, repId, activeOnly: false, ct: ct))
            .Select(a => a.MemberId).Distinct().ToList();

        await _publisher.PublishStatusChangedAsync(
            updated, fromStatus: from, toStatus: PersonalRepStatus.Inactive,
            associatedMemberIds: memberIds,
            actor, HttpContext.TraceIdentifier, ct);

        var result = await DecryptForResponseAsync(updated, ct);
        return Ok(result);
    }

    [HttpPost("{repId}/associations")]
    [ProducesResponseType(typeof(PersonalRepAssociation), 201)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> AddAssociation(
        [FromRoute] string repId,
        [FromBody] AddAssociationRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var rep = await _reps.GetByIdAsync(TenantId, repId, ct);
        if (rep == null) return NotFound();

        var existing = await _reps.FindActiveAssociationAsync(TenantId, repId, request.MemberId, ct);
        if (existing != null) return Conflict(new ProblemDetails
        {
            Status = 409,
            Title = "Association already exists",
            Detail = $"Rep {repId} already has an active association with member {request.MemberId}."
        });

        var actor = Actor;
        var pairId = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        var forward = new PersonalRepAssociation
        {
            TenantId = TenantId,
            PairId = pairId,
            RepId = repId,
            MemberId = request.MemberId,
            Direction = AssociationDirection.RepToMember,
            CredentialType = rep.CredentialType,
            EffectiveFrom = request.EffectiveFrom ?? now,
            EffectiveTo = request.EffectiveTo,
            CreatedAt = now,
            CreatedBy = actor
        };
        var inverse = new PersonalRepAssociation
        {
            TenantId = TenantId,
            PairId = pairId,
            RepId = repId,
            MemberId = request.MemberId,
            Direction = AssociationDirection.MemberToRep,
            CredentialType = rep.CredentialType,
            EffectiveFrom = forward.EffectiveFrom,
            EffectiveTo = forward.EffectiveTo,
            CreatedAt = now,
            CreatedBy = actor
        };

        var auditEvent = BuildRepEvent(rep, PersonalRepEventType.PersonalRepAssociationAdded,
            fromStatus: null, toStatus: null, actor, request.EventId, memberId: request.MemberId);
        auditEvent.Payload = new JsonObject
        {
            ["memberId"] = request.MemberId,
            ["pairId"] = pairId,
            ["credentialType"] = rep.CredentialType.ToString(),
            ["effectiveFrom"] = forward.EffectiveFrom.ToString("o"),
            ["effectiveTo"] = forward.EffectiveTo?.ToString("o")
        };

        await _reps.AddAssociationPairAsync(forward, inverse, auditEvent, ct);
        await _publisher.PublishAssociationChangedAsync(
            rep, forward, PersonalRepEventType.PersonalRepAssociationAdded,
            actor, HttpContext.TraceIdentifier, ct);

        return CreatedAtAction(nameof(GetRepresentative),
            new { repId }, forward);
    }

    [HttpDelete("{repId}/associations/{memberId}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> RemoveAssociation(
        [FromRoute] string repId,
        [FromRoute] string memberId,
        [FromBody] RemoveAssociationRequest? request,
        CancellationToken ct)
    {
        var rep = await _reps.GetByIdAsync(TenantId, repId, ct);
        if (rep == null) return NotFound();

        var existing = await _reps.FindActiveAssociationAsync(TenantId, repId, memberId, ct);
        if (existing == null) return NotFound();

        var actor = Actor;

        var auditEvent = BuildRepEvent(rep, PersonalRepEventType.PersonalRepAssociationRemoved,
            fromStatus: null, toStatus: null, actor, request?.EventId, memberId);
        auditEvent.Payload = new JsonObject
        {
            ["memberId"] = memberId,
            ["pairId"] = existing.PairId,
            ["credentialType"] = existing.CredentialType.ToString()
        };

        await _reps.RemoveAssociationPairAsync(TenantId, existing.PairId, actor, auditEvent, ct);
        await _publisher.PublishAssociationChangedAsync(
            rep, existing, PersonalRepEventType.PersonalRepAssociationRemoved,
            actor, HttpContext.TraceIdentifier, ct);

        return NoContent();
    }

    /// <summary>
    /// Race-safe persistence of Active → Inactive when the caller observes
    /// that <c>ExpiresAt</c> has passed on a persisted-Active record. If
    /// this caller wins the race, the audit event is appended and a Kafka
    /// event emitted. Lost race is a silent no-op — the audit trail still
    /// gets exactly one event.
    /// </summary>
    private async Task<PersonalRepresentative> MaybeObserveExpiryAsync(
        PersonalRepresentative rep, CancellationToken ct)
    {
        if (rep.ObservedStatus() != PersonalRepStatus.Inactive) return rep;
        if (rep.Status != PersonalRepStatus.Active) return rep;

        var actor = "System";
        var auditEvent = BuildRepEvent(rep, PersonalRepEventType.PersonalRepExpired,
            fromStatus: PersonalRepStatus.Active, toStatus: PersonalRepStatus.Inactive,
            actor, eventId: null, memberId: null);

        var persistedRep = await _reps.TryTransitionToInactiveAsync(rep, auditEvent);
        if (persistedRep != null)
        {
            var memberIds = (await _reps.ListAssociationsForRepAsync(TenantId, persistedRep.Id, activeOnly: false, ct: ct))
                .Select(a => a.MemberId).Distinct().ToList();
            await _publisher.PublishStatusChangedAsync(
                persistedRep, fromStatus: PersonalRepStatus.Active, toStatus: PersonalRepStatus.Inactive,
                associatedMemberIds: memberIds,
                actor, HttpContext.TraceIdentifier, ct);
        }

        rep.Status = PersonalRepStatus.Inactive;
        rep.InactivationReasonCode = PersonalRepInactivationReasonCode.Expired;
        return persistedRep ?? rep;
    }

    private static PersonalRepEvent BuildRepEvent(
        PersonalRepresentative rep,
        PersonalRepEventType type,
        PersonalRepStatus? fromStatus,
        PersonalRepStatus? toStatus,
        string actor,
        string? eventId,
        string? memberId)
    {
        var payload = new JsonObject
        {
            ["personalRepId"] = rep.Id,
            ["credentialType"] = rep.CredentialType.ToString(),
            ["fromStatus"] = fromStatus?.ToString(),
            ["toStatus"] = toStatus?.ToString(),
            ["effectiveFrom"] = rep.EffectiveFrom?.ToString("o"),
            ["effectiveTo"] = rep.EffectiveTo?.ToString("o"),
            ["expiresAt"] = rep.ExpiresAt?.ToString("o"),
            ["inactivationReasonCode"] = rep.InactivationReasonCode?.ToString()
        };

        return new PersonalRepEvent
        {
            TenantId = rep.TenantId,
            PersonalRepId = rep.Id,
            MemberId = memberId,
            EventId = string.IsNullOrEmpty(eventId) ? Guid.NewGuid().ToString() : eventId,
            EventType = type,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActorId = actor,
            OccurredAt = DateTime.UtcNow,
            Payload = payload
        };
    }

    private async Task<PersonalRepresentative> DecryptForResponseAsync(
        PersonalRepresentative rep, CancellationToken ct)
    {
        return new PersonalRepresentative
        {
            TenantId = rep.TenantId,
            Id = rep.Id,
            Status = rep.ObservedStatus(),
            CredentialType = rep.CredentialType,
            EffectiveFrom = rep.EffectiveFrom,
            EffectiveTo = rep.EffectiveTo,
            ExpiresAt = rep.ExpiresAt,
            ProofOfAuthorityDocumentId = rep.ProofOfAuthorityDocumentId,
            ProofOfAuthorityVerifiedBy = rep.ProofOfAuthorityVerifiedBy,
            ProofOfAuthorityVerifiedAt = rep.ProofOfAuthorityVerifiedAt,
            FirstName = await _encryptor.DecryptAsync(rep.FirstName, ct),
            MiddleName = await _encryptor.DecryptAsync(rep.MiddleName, ct),
            LastName = await _encryptor.DecryptAsync(rep.LastName, ct),
            Email = await _encryptor.DecryptAsync(rep.Email, ct),
            PhoneNumber = await _encryptor.DecryptAsync(rep.PhoneNumber, ct),
            MailingAddressLine1 = await _encryptor.DecryptAsync(rep.MailingAddressLine1, ct),
            MailingAddressLine2 = await _encryptor.DecryptAsync(rep.MailingAddressLine2, ct),
            MailingAddressCity = await _encryptor.DecryptAsync(rep.MailingAddressCity, ct),
            MailingAddressStateCode = await _encryptor.DecryptAsync(rep.MailingAddressStateCode, ct),
            MailingAddressPostalCode = await _encryptor.DecryptAsync(rep.MailingAddressPostalCode, ct),
            RelationshipNotes = await _encryptor.DecryptAsync(rep.RelationshipNotes, ct),
            CreatedAt = rep.CreatedAt,
            CreatedBy = rep.CreatedBy,
            UpdatedAt = rep.UpdatedAt,
            UpdatedBy = rep.UpdatedBy,
            ActivatedBy = rep.ActivatedBy,
            ActivatedAt = rep.ActivatedAt,
            InactivatedBy = rep.InactivatedBy,
            InactivatedAt = rep.InactivatedAt,
            InactivationReasonCode = rep.InactivationReasonCode
        };
    }

    private IActionResult ConflictTransition(InvalidPersonalRepTransitionException ex)
    {
        var problem = new ProblemDetails
        {
            Status = 409,
            Title = "Invalid personal representative transition",
            Detail = ex.Message,
            Type = "https://cloudhealthoffice.com/problems/personal-rep-transition"
        };
        problem.Extensions["fromStatus"] = ex.FromStatus.ToString();
        problem.Extensions["toStatus"] = ex.ToStatus.ToString();
        return Conflict(problem);
    }
}

public class CreatePersonalRepRequest
{
    [Required]
    public PersonalRepCredentialType CredentialType { get; set; }

    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public DateTime? ExpiresAt { get; set; }

    [StringLength(100)]
    public string? ProofOfAuthorityDocumentId { get; set; }

    [StringLength(500)]
    public string? FirstName { get; set; }
    [StringLength(500)]
    public string? MiddleName { get; set; }
    [StringLength(500)]
    public string? LastName { get; set; }
    [StringLength(500)]
    public string? Email { get; set; }
    [StringLength(100)]
    public string? PhoneNumber { get; set; }
    [StringLength(500)]
    public string? MailingAddressLine1 { get; set; }
    [StringLength(500)]
    public string? MailingAddressLine2 { get; set; }
    [StringLength(200)]
    public string? MailingAddressCity { get; set; }
    [StringLength(50)]
    public string? MailingAddressStateCode { get; set; }
    [StringLength(20)]
    public string? MailingAddressPostalCode { get; set; }
    [StringLength(4000)]
    public string? RelationshipNotes { get; set; }

    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class ActivatePersonalRepRequest
{
    /// <summary>
    /// The member-document-service id of the guardianship order, power of
    /// attorney or surrogate designation. Required (here or on the
    /// representative) for LegalGuardian, HealthcarePowerOfAttorney and
    /// HealthcareSurrogate; replaces the id recorded at creation.
    /// </summary>
    [StringLength(100)]
    public string? ProofOfAuthorityDocumentId { get; set; }

    public string? EventId { get; set; }
}

public class RevokePersonalRepRequest
{
    public PersonalRepInactivationReasonCode? ReasonCode { get; set; }
    public string? EventId { get; set; }
}

public class AddAssociationRequest
{
    [Required]
    [StringLength(50)]
    public string MemberId { get; set; } = string.Empty;

    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    public string? EventId { get; set; }
}

public class RemoveAssociationRequest
{
    public string? EventId { get; set; }
}

public class PersonalRepHistoryResponse
{
    public List<PersonalRepEvent> Items { get; set; } = new();
}
