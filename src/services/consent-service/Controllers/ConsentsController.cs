using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using ConsentService.Middleware;
using ConsentService.Models;
using ConsentService.Repositories;
using ConsentService.Services;
using Microsoft.AspNetCore.Mvc;

// TODO(feature-5.18-followup): scope-based access enforcement / integration
// with authorization-service. In this PR the controller enforces tenant
// isolation only; whether the caller has been granted authority over
// {memberId}'s consent record is out of scope.

namespace ConsentService.Controllers;

/// <summary>
/// HIPAA §164.508 authorization records. Consents are never deleted; the
/// lifecycle is Draft -> Active -> Revoked / Expired, with an append-only
/// audit trail on every transition.
/// </summary>
[ApiController]
[Route("api/v1/members/{memberId}/consents")]
public class ConsentsController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IConsentRepository _consents;
    private readonly IConsentEventRepository _events;
    private readonly IConsentFieldEncryptor _encryptor;
    private readonly IConsentEventPublisher _publisher;
    private readonly ILogger<ConsentsController>? _logger;

    public ConsentsController(
        IConsentRepository consents,
        IConsentEventRepository events,
        IConsentFieldEncryptor encryptor,
        IConsentEventPublisher publisher,
        ILogger<ConsentsController>? logger = null)
    {
        _consents = consents;
        _events = events;
        _encryptor = encryptor;
        _publisher = publisher;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(Consent), 201)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> CreateConsent(
        [FromRoute] string memberId,
        [FromBody] CreateConsentRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var actor = User.Identity?.Name ?? "System";

        var consent = new Consent
        {
            TenantId = TenantId,
            MemberId = memberId,
            ConsentType = request.ConsentType,
            SensitiveCategory = request.SensitiveCategory,
            Status = ConsentStatus.Draft,
            EffectiveAt = request.EffectiveAt,
            ExpiresAt = request.ExpiresAt,
            GrantedBy = request.GrantedBy,
            Reason = await _encryptor.EncryptAsync(request.Reason, ct),
            GrantedToName = await _encryptor.EncryptAsync(request.GrantedToName, ct),
            GrantedToContact = await _encryptor.EncryptAsync(request.GrantedToContact, ct),
            Purpose = await _encryptor.EncryptAsync(request.Purpose, ct),
            CreatedAt = DateTime.UtcNow
        };

        var genesis = BuildEvent(consent, ConsentEventType.ConsentCreated, fromStatus: null,
            toStatus: ConsentStatus.Draft, actor, request.EventId);

        var created = await _consents.CreateAsync(consent, genesis);
        await _publisher.PublishStatusChangedAsync(
            created, fromStatus: null, toStatus: ConsentStatus.Draft, actor, HttpContext.TraceIdentifier, ct);

        var view = await DecryptForResponseAsync(created, ct);
        return CreatedAtAction(nameof(GetConsent),
            new { memberId, consentId = created.Id }, view);
    }

    [HttpGet("{consentId}")]
    [ProducesResponseType(typeof(Consent), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetConsent(
        [FromRoute] string memberId,
        [FromRoute] string consentId,
        CancellationToken ct)
    {
        var consent = await _consents.GetByIdAsync(TenantId, memberId, consentId);
        if (consent == null) return NotFound();

        consent = await MaybeObserveExpiryAsync(consent, ct);

        var view = await DecryptForResponseAsync(consent, ct);
        return Ok(view);
    }

    [HttpGet]
    [ProducesResponseType(typeof(ConsentListResponse), 200)]
    public async Task<IActionResult> ListByMember(
        [FromRoute] string memberId,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var activeOnly = string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
        var items = await _consents.ListByMemberAsync(TenantId, memberId, activeOnly);

        // Per-consent decrypts are independent and hit the same in-memory
        // key cache after the first miss — fan out via Task.WhenAll so a
        // large list does not pay N sequential round-trips.
        var decrypted = await Task.WhenAll(items.Select(c => DecryptForResponseAsync(c, ct)));

        return Ok(new ConsentListResponse { Items = decrypted.ToList() });
    }

    [HttpGet("{consentId}/history")]
    [ProducesResponseType(typeof(ConsentHistoryResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetHistory(
        [FromRoute] string memberId,
        [FromRoute] string consentId,
        CancellationToken ct)
    {
        var consent = await _consents.GetByIdAsync(TenantId, memberId, consentId);
        if (consent == null) return NotFound();

        var events = await _events.ListByConsentAsync(TenantId, consentId, ct);
        return Ok(new ConsentHistoryResponse { Items = events.ToList() });
    }

    [HttpPost("{consentId}/activate")]
    [ProducesResponseType(typeof(Consent), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> Activate(
        [FromRoute] string memberId,
        [FromRoute] string consentId,
        [FromBody] ActivateConsentRequest? request,
        CancellationToken ct)
    {
        var consent = await _consents.GetByIdAsync(TenantId, memberId, consentId);
        if (consent == null) return NotFound();

        // Observe read-time expiry BEFORE the idempotency/state-machine
        // checks so an Active record whose ExpiresAt has passed is
        // persisted as Expired and correctly rejected here rather than
        // silently transitioning Active -> Active.
        consent = await MaybeObserveExpiryAsync(consent, ct);

        // Idempotent no-op when already Active. No second event, no second Kafka.
        if (consent.Status == ConsentStatus.Active)
        {
            var view = await DecryptForResponseAsync(consent, ct);
            return Ok(view);
        }

        try
        {
            ConsentStateMachine.EnsureAllowed(consent.Status, ConsentStatus.Active);
        }
        catch (InvalidConsentTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        var actor = User.Identity?.Name ?? "System";
        var from = consent.Status;
        consent.Status = ConsentStatus.Active;
        consent.ActivatedBy = actor;
        consent.ActivatedAt = DateTime.UtcNow;
        if (!consent.EffectiveAt.HasValue)
            consent.EffectiveAt = consent.ActivatedAt;

        var auditEvent = BuildEvent(consent, ConsentEventType.ConsentActivated,
            fromStatus: from, toStatus: ConsentStatus.Active, actor, request?.EventId);

        Consent updated;
        try
        {
            updated = await _consents.TransitionStatusAsync(consent, auditEvent);
        }
        catch (InvalidConsentTransitionException ex)
        {
            // Repository detected that another writer transitioned this
            // record first between our read and write — surface as 409.
            return ConflictTransition(ex);
        }

        await _publisher.PublishStatusChangedAsync(
            updated, fromStatus: from, toStatus: ConsentStatus.Active, actor, HttpContext.TraceIdentifier, ct);

        var result = await DecryptForResponseAsync(updated, ct);
        return Ok(result);
    }

    [HttpPost("{consentId}/revoke")]
    [ProducesResponseType(typeof(Consent), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> Revoke(
        [FromRoute] string memberId,
        [FromRoute] string consentId,
        [FromBody] RevokeConsentRequest? request,
        CancellationToken ct)
    {
        var consent = await _consents.GetByIdAsync(TenantId, memberId, consentId);
        if (consent == null) return NotFound();

        // Observe read-time expiry BEFORE the idempotency/state-machine
        // checks. Without this, an Active record whose ExpiresAt has passed
        // would be silently revoked and the ConsentExpired audit row would
        // never be written — Expired is supposed to be terminal.
        consent = await MaybeObserveExpiryAsync(consent, ct);

        // Idempotent no-op when already Revoked. Second call = 200, no
        // second event, no second Kafka.
        if (consent.Status == ConsentStatus.Revoked)
        {
            var view = await DecryptForResponseAsync(consent, ct);
            return Ok(view);
        }

        try
        {
            ConsentStateMachine.EnsureAllowed(consent.Status, ConsentStatus.Revoked);
        }
        catch (InvalidConsentTransitionException ex)
        {
            return ConflictTransition(ex);
        }

        var actor = User.Identity?.Name ?? "System";
        var from = consent.Status;
        consent.Status = ConsentStatus.Revoked;
        consent.RevokedBy = actor;
        consent.RevokedAt = DateTime.UtcNow;
        consent.RevocationReasonCode = request?.ReasonCode ?? ConsentRevocationReasonCode.MemberRequest;

        var auditEvent = BuildEvent(consent, ConsentEventType.ConsentRevoked,
            fromStatus: from, toStatus: ConsentStatus.Revoked, actor, request?.EventId);

        Consent updated;
        try
        {
            updated = await _consents.TransitionStatusAsync(consent, auditEvent);
        }
        catch (InvalidConsentTransitionException ex)
        {
            // Repository detected that another writer transitioned this
            // record first between our read and write — surface as 409.
            return ConflictTransition(ex);
        }

        await _publisher.PublishStatusChangedAsync(
            updated, fromStatus: from, toStatus: ConsentStatus.Revoked, actor, HttpContext.TraceIdentifier, ct);

        var result = await DecryptForResponseAsync(updated, ct);
        return Ok(result);
    }

    /// <summary>
    /// When a read observes that a persisted-Active consent has passed its
    /// expiry, persist the transition via a conditional write AND append
    /// the <c>ConsentExpired</c> audit event — exactly once even under
    /// concurrent reads. A lost race (someone else expired it first) is a
    /// silent no-op; the audit trail still gets exactly one event.
    ///
    /// Returns the (possibly-mutated) consent so callers that need to
    /// base idempotency / state-machine checks on the post-expiry state
    /// can do so with a single chained call.
    /// </summary>
    private async Task<Consent> MaybeObserveExpiryAsync(Consent consent, CancellationToken ct)
    {
        if (consent.ObservedStatus() != ConsentStatus.Expired) return consent;
        if (consent.Status != ConsentStatus.Active) return consent;

        var actor = "System";
        var auditEvent = BuildEvent(consent, ConsentEventType.ConsentExpired,
            fromStatus: ConsentStatus.Active, toStatus: ConsentStatus.Expired, actor, eventId: null);

        var persisted = await _consents.TryTransitionToExpiredAsync(consent, auditEvent);
        if (persisted)
        {
            await _publisher.PublishStatusChangedAsync(
                consent, fromStatus: ConsentStatus.Active, toStatus: ConsentStatus.Expired,
                actor, HttpContext.TraceIdentifier, ct);
        }

        // Reflect the observed terminal state on the caller's local copy
        // regardless of who won the race — the record IS Expired from the
        // caller's perspective, and the idempotency / state-machine checks
        // below must see that, not the pre-expiry Active snapshot.
        consent.Status = ConsentStatus.Expired;
        consent.RevocationReasonCode = ConsentRevocationReasonCode.Expired;
        return consent;
    }

    private static ConsentEvent BuildEvent(
        Consent consent,
        ConsentEventType type,
        ConsentStatus? fromStatus,
        ConsentStatus toStatus,
        string actor,
        string? eventId)
    {
        var payload = new JsonObject
        {
            ["consentId"] = consent.Id,
            ["consentType"] = consent.ConsentType.ToString(),
            ["sensitiveCategory"] = consent.SensitiveCategory,
            ["fromStatus"] = fromStatus?.ToString(),
            ["toStatus"] = toStatus.ToString(),
            ["effectiveAt"] = consent.EffectiveAt?.ToString("o"),
            ["expiresAt"] = consent.ExpiresAt?.ToString("o"),
            ["revocationReasonCode"] = consent.RevocationReasonCode?.ToString()
        };

        return new ConsentEvent
        {
            TenantId = consent.TenantId,
            ConsentId = consent.Id,
            MemberId = consent.MemberId,
            EventId = string.IsNullOrEmpty(eventId) ? Guid.NewGuid().ToString() : eventId,
            EventType = type,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            ActorId = actor,
            OccurredAt = DateTime.UtcNow,
            Payload = payload
        };
    }

    private async Task<Consent> DecryptForResponseAsync(Consent consent, CancellationToken ct)
    {
        // Shallow copy — leave the stored record untouched; decrypt the
        // outward-facing view only. Avoids aliasing bugs where a caller
        // persists the decrypted copy.
        return new Consent
        {
            TenantId = consent.TenantId,
            Id = consent.Id,
            MemberId = consent.MemberId,
            ConsentType = consent.ConsentType,
            SensitiveCategory = consent.SensitiveCategory,
            Status = consent.ObservedStatus(),
            EffectiveAt = consent.EffectiveAt,
            ExpiresAt = consent.ExpiresAt,
            GrantedBy = consent.GrantedBy,
            Reason = await _encryptor.DecryptAsync(consent.Reason, ct),
            GrantedToName = await _encryptor.DecryptAsync(consent.GrantedToName, ct),
            GrantedToContact = await _encryptor.DecryptAsync(consent.GrantedToContact, ct),
            Purpose = await _encryptor.DecryptAsync(consent.Purpose, ct),
            CreatedAt = consent.CreatedAt,
            ActivatedBy = consent.ActivatedBy,
            ActivatedAt = consent.ActivatedAt,
            RevokedBy = consent.RevokedBy,
            RevokedAt = consent.RevokedAt,
            RevocationReasonCode = consent.RevocationReasonCode
        };
    }

    private IActionResult ConflictTransition(InvalidConsentTransitionException ex)
    {
        var problem = new ProblemDetails
        {
            Status = 409,
            Title = "Invalid consent transition",
            Detail = ex.Message,
            Type = "https://cloudhealthoffice.com/problems/consent-transition"
        };
        problem.Extensions["fromStatus"] = ex.FromStatus.ToString();
        problem.Extensions["toStatus"] = ex.ToStatus.ToString();
        return Conflict(problem);
    }
}

public class CreateConsentRequest
{
    [Required]
    public ConsentType ConsentType { get; set; }

    [StringLength(100)]
    public string? SensitiveCategory { get; set; }

    public DateTime? EffectiveAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    [Required]
    [StringLength(200)]
    public string GrantedBy { get; set; } = string.Empty;

    [StringLength(4000)]
    public string? Reason { get; set; }

    [StringLength(500)]
    public string? GrantedToName { get; set; }

    [StringLength(1000)]
    public string? GrantedToContact { get; set; }

    [StringLength(2000)]
    public string? Purpose { get; set; }

    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class ActivateConsentRequest
{
    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class RevokeConsentRequest
{
    public ConsentRevocationReasonCode? ReasonCode { get; set; }

    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class ConsentListResponse
{
    public List<Consent> Items { get; set; } = new();
}

public class ConsentHistoryResponse
{
    public List<ConsentEvent> Items { get; set; } = new();
}
