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
/// Member management API — manages health plan subscribers and dependents.
/// Data populated by X12 834 Enrollment transactions; surfaced as FHIR R4 Patient.
/// </summary>
[ApiController]
[Route("api/v1/members")]
public class MembersController : ControllerBase
{
    // Tenant context from middleware
    private string TenantId => HttpContext.GetTenantId();

    private readonly IMemberRepository _memberRepository;
    private readonly IMemberEventPublisher _eventPublisher;
    private readonly IMemberEventRepository _eventRepository;
    private readonly IFhirPatientProjector _fhirProjector;
    private readonly IIdentifierEncryptor _encryptor;
    private readonly ICoverageServiceClient _coverage;
    private readonly IEnrollmentImportServiceClient _enrollment;
    private readonly IAccumulatorServiceClient _accumulators;
    private readonly IRelationshipShim? _relationshipShim;
    private readonly IFamilyRelationshipService? _familyRelationships;
    private readonly IMemberAlertGuard? _alertGuard;
    private readonly ILogger<MembersController>? _logger;

    public MembersController(
        IMemberRepository memberRepository,
        IMemberEventPublisher eventPublisher,
        IMemberEventRepository eventRepository,
        IFhirPatientProjector fhirProjector,
        IIdentifierEncryptor encryptor,
        ICoverageServiceClient coverage,
        IEnrollmentImportServiceClient enrollment,
        IAccumulatorServiceClient accumulators,
        IRelationshipShim? relationshipShim = null,
        IFamilyRelationshipService? familyRelationships = null,
        IMemberAlertGuard? alertGuard = null,
        ILogger<MembersController>? logger = null)
    {
        _memberRepository = memberRepository;
        _eventPublisher = eventPublisher;
        _eventRepository = eventRepository;
        _fhirProjector = fhirProjector;
        _encryptor = encryptor;
        _coverage = coverage;
        _enrollment = enrollment;
        _accumulators = accumulators;
        _relationshipShim = relationshipShim;
        _familyRelationships = familyRelationships;
        _alertGuard = alertGuard;
        _logger = logger;
    }

    // ── Search / read ────────────────────────────────────────────────

    /// <summary>Search members by various criteria.</summary>
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
        if (!string.IsNullOrEmpty(memberId))
        {
            var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
            // Drafts are hidden from standard search paths (parity with GetMember).
            if (member != null && member.IsDraft) member = null;
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

        var list = items.Where(m => !m.IsDraft).ToList();
        return Ok(new MemberListResponse
        {
            Members = list,
            ContinuationToken = token,
            TotalCount = list.Count
        });
    }

    /// <summary>Free-text search across memberId and lastName.</summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(List<Member>), 200)]
    public async Task<IActionResult> SearchByQuery([FromQuery] string? q = null)
    {
        if (string.IsNullOrWhiteSpace(q))
            return await SearchMembers(pageSize: 20);

        var byId = await _memberRepository.GetByMemberIdAsync(TenantId, q);
        if (byId != null && !byId.IsDraft)
            return Ok(new List<Member> { byId });

        return await SearchMembers(
            memberId: null, groupNumber: null, subscriberId: null,
            lastName: q, dateOfBirth: null, activeOnly: false,
            subscribersOnly: false, pageSize: 20, continuationToken: null);
    }

    /// <summary>Get member details by member ID.</summary>
    [HttpGet("{memberId}")]
    [ProducesResponseType(typeof(Member), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetMember([FromRoute] string memberId)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();
        if (member.IsDraft) return NotFound();
        await HydrateSubscriberFromGraphAsync(member, HttpContext.RequestAborted);
        return Ok(member);
    }

    /// <summary>
    /// Overwrite the legacy <see cref="Member.SubscriberMemberId"/> from the active
    /// <see cref="FamilyRelationship"/> graph when one is registered. Non-throwing: if
    /// derivation fails or returns null, the stored legacy value remains.
    /// </summary>
    private async Task HydrateSubscriberFromGraphAsync(Member member, CancellationToken ct)
    {
        if (_familyRelationships == null) return;
        if (member.IsSubscriber) return;
        try
        {
            var derived = await _familyRelationships.DeriveSubscriberMemberIdAsync(TenantId, member.MemberId, ct);
            if (!string.IsNullOrEmpty(derived))
            {
#pragma warning disable CS0618
                member.SubscriberMemberId = derived;
#pragma warning restore CS0618
            }
        }
        catch (Exception ex)
        {
            // Best-effort on the read path — the legacy field still holds the last 834
            // value, so the GET doesn't need to fail. Log so operators can see when the
            // graph is unavailable for a given tenant instead of silently falling through.
            _logger?.LogWarning(ex,
                "Best-effort SubscriberMemberId derivation failed for tenant {TenantId} member {MemberId}; returning legacy field.",
                TenantId, member.MemberId);
        }
    }

    // ── Create / update / terminate ──────────────────────────────────

    /// <summary>Create a new member. Idempotent on MemberId within a tenant.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(Member), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> CreateMember(
        [FromBody] CreateMemberRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        if (!string.IsNullOrEmpty(request.SubscriberMemberId))
        {
            var subscriber = await _memberRepository.GetByMemberIdAsync(TenantId, request.SubscriberMemberId);
            if (subscriber == null)
                return BadRequest($"Subscriber '{request.SubscriberMemberId}' not found in tenant.");
        }

        var existing = await _memberRepository.GetByMemberIdAsync(TenantId, request.MemberId);
        if (existing != null)
            return Conflict(new { memberId = request.MemberId, message = "MemberId already exists in this tenant." });

        var identifiers = new List<MemberIdentifier>();
        if (!string.IsNullOrEmpty(request.SSN))
        {
            var cipher = await _encryptor.EncryptAsync(request.SSN, ct);
            identifiers.Add(new MemberIdentifier
            {
                Type = MemberIdentifierType.SSN,
                System = FhirIdentifierSystems.SSN,
                Value = cipher ?? string.Empty,
                IsEncrypted = _encryptor.IsEnabled
            });
        }

#pragma warning disable CS0618 // write-back to legacy FK preserves 834-shaped payloads; graph is authoritative going forward
        var member = new Member
        {
            TenantId = TenantId,
            MemberId = request.MemberId,
            SSN = _encryptor.IsEnabled ? null : request.SSN,
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
            PlanChangeEffectiveDate = request.PlanChangeEffectiveDate,
            MedicaidSpendDownLiabilityAmount = request.MedicaidSpendDownLiabilityAmount,
            MedicaidSpendDownAmountMet = request.MedicaidSpendDownAmountMet,
            EmploymentStatus = request.EmploymentStatus,
            TobaccoUser = request.TobaccoUser,
            IsStudent = request.IsStudent,
            Identifiers = identifiers,
            PreferredLanguage = request.PreferredLanguage,
            BirthSex = request.BirthSex,
            CreatedDate = DateTime.UtcNow,
            LastUpdatedDate = DateTime.UtcNow,
            CreatedBy = User.Identity?.Name ?? "System"
        };
#pragma warning restore CS0618

        await _memberRepository.CreateAsync(member);

        // Shim the legacy FK onto the relationship graph for dependents. No-op when
        // the shim isn't registered (tests) or the member is a subscriber.
        if (_relationshipShim != null)
        {
            await _relationshipShim.EnsureRelationshipAsync(member, User.Identity?.Name, ct);
        }

        var eventId = !string.IsNullOrEmpty(request.EventId)
            ? request.EventId
            : Guid.NewGuid().ToString();

        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = member.MemberId,
            EventId = eventId,
            EventType = MemberEventType.MemberCreated,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = SnapshotPayload(member)   // genesis = full snapshot
        }, ct);

        return CreatedAtAction(nameof(GetMember), new { memberId = member.MemberId }, member);
    }

    /// <summary>Update member information. Emits MemberUpdated and, if the address changed, AddressChanged.</summary>
    [HttpPut("{memberId}")]
    [ProducesResponseType(typeof(Member), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> UpdateMember(
        [FromRoute] string memberId,
        [FromBody] UpdateMemberRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var diff = new JsonObject();
        var addressDiff = new JsonObject();

        if (request.Address != null && request.Address != member.Address)
        { diff["address"] = request.Address; addressDiff["address"] = request.Address; member.Address = request.Address; }
        if (request.City != null && request.City != member.City)
        { diff["city"] = request.City; addressDiff["city"] = request.City; member.City = request.City; }
        if (request.State != null && request.State != member.State)
        { diff["state"] = request.State; addressDiff["state"] = request.State; member.State = request.State; }
        if (request.ZipCode != null && request.ZipCode != member.ZipCode)
        { diff["zipCode"] = request.ZipCode; addressDiff["zipCode"] = request.ZipCode; member.ZipCode = request.ZipCode; }
        if (request.Phone != null && request.Phone != member.Phone)
        { diff["phone"] = request.Phone; member.Phone = request.Phone; }
        if (request.Email != null && request.Email != member.Email)
        { diff["email"] = request.Email; member.Email = request.Email; }
        if (request.Status.HasValue && request.Status.Value != member.Status)
        { diff["status"] = request.Status.Value.ToString(); member.Status = request.Status.Value; }
        if (request.EmploymentStatus.HasValue && request.EmploymentStatus.Value != member.EmploymentStatus)
        { diff["employmentStatus"] = request.EmploymentStatus.Value.ToString(); member.EmploymentStatus = request.EmploymentStatus.Value; }

        member.LastUpdatedDate = DateTime.UtcNow;
        member.LastUpdatedBy = User.Identity?.Name ?? "System";

        if (diff.Count == 0) return Ok(member);

        await _memberRepository.UpdateAsync(member);

        // Parent event id is the anchor for any sub-events spawned from this update.
        // Re-posting the same UpdateMemberRequest (same EventId) must produce the same
        // set of events — so sub-event ids are deterministic suffixes of the parent.
        var parentEventId = request.EventId ?? Guid.NewGuid().ToString();

        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = member.MemberId,
            EventId = parentEventId,
            EventType = MemberEventType.MemberUpdated,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = diff
        }, ct);

        if (addressDiff.Count > 0)
        {
            await _eventPublisher.PublishAsync(new MemberEvent
            {
                TenantId = TenantId,
                MemberId = member.MemberId,
                EventId = $"{parentEventId}:address",
                EventType = MemberEventType.AddressChanged,
                ActorId = User.Identity?.Name,
                CorrelationId = HttpContext.TraceIdentifier,
                Payload = addressDiff
            }, ct);
        }

        return Ok(member);
    }

    /// <summary>Terminate member coverage (DELETE variant). Equivalent to the body-based <c>/terminate</c> endpoint.</summary>
    [HttpDelete("{memberId}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    public async Task<IActionResult> TerminateMember(
        [FromRoute] string memberId,
        [FromQuery] DateTime? terminationDate = null,
        [FromQuery] string? reasonCode = null,
        [FromQuery] string? eventId = null,
        CancellationToken ct = default)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var block = await EvaluateTerminationGuardAsync(memberId, ct);
        if (block != null) return AlertBlocked(block);

        await TerminateInternal(member, terminationDate ?? DateTime.UtcNow, reasonCode, eventId, ct);
        return NoContent();
    }

    /// <summary>Terminate member coverage (body-based variant used by portal).</summary>
    [HttpPost("{memberId}/terminate")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(typeof(ProblemDetails), 409)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> TerminateMember(
        [FromRoute] string memberId,
        [FromBody] TerminateMemberRequest request,
        CancellationToken ct)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var block = await EvaluateTerminationGuardAsync(memberId, ct);
        if (block != null) return AlertBlocked(block);

        await TerminateInternal(member, request.TerminationDate, request.ReasonCode, request.EventId, ct);

        try
        {
            await _coverage.TerminateCoverageAsync(TenantId, memberId, request, ct);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }

        return Ok(new { memberId, terminationDate = request.TerminationDate, reasonCode = request.ReasonCode });
    }

    private async Task<MemberAlertBlock?> EvaluateTerminationGuardAsync(string memberId, CancellationToken ct)
    {
        if (_alertGuard == null) return null;
        return await _alertGuard.EvaluateAsync(TenantId, memberId, MemberAlertAction.Terminate, ct);
    }

    private IActionResult AlertBlocked(MemberAlertBlock block)
    {
        var problem = new ProblemDetails
        {
            Type = "https://cloudhealthoffice.com/problems/member-alert-block",
            Title = "Action blocked by active member alert",
            Status = StatusCodes.Status409Conflict,
            Detail = block.Reason
        };
        problem.Extensions["alertId"] = block.Alert.Id;
        problem.Extensions["alertType"] = block.Alert.AlertType.ToString();
        problem.Extensions["severity"] = block.Alert.Severity.ToString();
        problem.Extensions["action"] = block.Action.ToString();
        if (!string.IsNullOrEmpty(block.Alert.RequiredAction))
        {
            problem.Extensions["requiredAction"] = block.Alert.RequiredAction;
        }
        return StatusCode(StatusCodes.Status409Conflict, problem);
    }

    private async Task TerminateInternal(
        Member member,
        DateTime terminationDate,
        string? reasonCode,
        string? eventId,
        CancellationToken ct)
    {
        member.Status = EnrollmentStatus.Terminated;
        member.TerminationDate = terminationDate;
        if (!string.IsNullOrEmpty(reasonCode)) member.MaintenanceReasonCode = reasonCode;
        member.LastUpdatedDate = DateTime.UtcNow;
        member.LastUpdatedBy = User.Identity?.Name ?? "System";

        await _memberRepository.UpdateAsync(member);

        var payload = new JsonObject
        {
            ["terminationDate"] = terminationDate.ToString("o"),
            ["reasonCode"] = reasonCode
        };

        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = member.MemberId,
            EventId = eventId ?? Guid.NewGuid().ToString(),
            EventType = MemberEventType.MemberTerminated,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = payload
        }, ct);
    }

    // ── Dependents ───────────────────────────────────────────────────

    [HttpGet("{memberId}/dependents")]
    [ProducesResponseType(typeof(List<Member>), 200)]
    public async Task<IActionResult> GetDependents([FromRoute] string memberId)
    {
        var dependents = await _memberRepository.GetDependentsAsync(TenantId, memberId);
        return Ok(dependents.Where(d => !d.IsDraft).ToList());
    }

    // ── Eligibility ──────────────────────────────────────────────────

    /// <summary>Verify member eligibility for a service date.</summary>
    [HttpGet("{memberId}/eligibility")]
    [ProducesResponseType(typeof(EligibilityCheckResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CheckEligibility(
        [FromRoute] string memberId,
        [FromQuery] DateTime? serviceDate = null)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var checkDate = (serviceDate ?? DateTime.UtcNow).Date;
        var effective = member.EffectiveDate.Date;
        var term = member.TerminationDate?.Date;

        bool isEligible;
        string reason;
        if (member.Status == EnrollmentStatus.Terminated)
        { isEligible = false; reason = "Coverage terminated"; }
        else if (checkDate < effective)
        { isEligible = false; reason = "Service date before effective date"; }
        else if (term.HasValue && checkDate > term.Value)
        { isEligible = false; reason = "Service date after termination date"; }
        else if (member.Status != EnrollmentStatus.Active)
        { isEligible = false; reason = $"Member status is {member.Status}"; }
        else
        { isEligible = true; reason = "Active coverage"; }

        return Ok(new EligibilityCheckResponse
        {
            MemberId = memberId,
            ServiceDate = checkDate,
            IsEligible = isEligible,
            Reason = reason,
            EffectiveDate = member.EffectiveDate,
            TerminationDate = member.TerminationDate
        });
    }

    // ── FHIR projection ──────────────────────────────────────────────

    /// <summary>FHIR R4 Patient projection of this member.</summary>
    [HttpGet("{memberId}/fhir")]
    [Produces("application/fhir+json", "application/json")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetFhirPatient([FromRoute] string memberId)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        // Best-effort PCP fetch so Patient.generalPractitioner is populated when
        // available. Failures (downstream unavailable, no PCP yet) silently
        // degrade to a Patient resource without generalPractitioner — that's
        // valid US Core; we don't 503 the FHIR read for a missing optional.
        MemberPcpResponse? pcp = null;
        try
        {
            pcp = await _coverage.GetPcpAsync(TenantId, memberId, HttpContext.RequestAborted);
        }
        catch (DownstreamUnavailableException ex)
        {
            _logger?.LogDebug(ex, "PCP lookup unavailable while projecting FHIR Patient for {MemberId}; emitting without generalPractitioner.", SanitizeForLog(memberId));
        }

        var patient = _fhirProjector.Project(member, pcp);
        return new ContentResult
        {
            ContentType = "application/fhir+json",
            Content = patient.ToJsonString(),
            StatusCode = 200
        };
    }

    // ── Event stream ─────────────────────────────────────────────────

    /// <summary>Return the member-events stream for this member, ordered by version.</summary>
    [HttpGet("{memberId}/events")]
    [ProducesResponseType(typeof(List<MemberEvent>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetEvents([FromRoute] string memberId, CancellationToken ct)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var events = await _eventRepository.ListByMemberAsync(TenantId, memberId, ct);
        return Ok(events);
    }

    // ── Portal integration endpoints ─────────────────────────────────

    [HttpGet("{memberId}/pcp")]
    [ProducesResponseType(typeof(MemberPcpResponse), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetMemberPcp([FromRoute] string memberId, CancellationToken ct)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        try
        {
            var pcp = await _coverage.GetPcpAsync(TenantId, memberId, ct);
            return Ok(pcp);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }
    }

    [HttpPut("{memberId}/pcp")]
    [ProducesResponseType(typeof(MemberPcpResponse), 200)]
    [ProducesResponseType(typeof(PcpValidationProblem), 400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> AssignPcp(
        [FromRoute] string memberId,
        [FromBody] AssignPcpRequest request,
        CancellationToken ct)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        // Forward the member's DOB so coverage-service can enforce age-range
        // panels (pediatric vs adult) without an extra round-trip.
        if (request.MemberDateOfBirth == null) request.MemberDateOfBirth = member.DateOfBirth;

        AssignPcpOutcome outcome;
        try
        {
            outcome = await _coverage.AssignPcpAsync(TenantId, memberId, request, ct);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }

        // Validation rejection — surface the structured error verbatim so the
        // portal can localize off the code. Do NOT publish a PcpChanged event.
        if (!outcome.IsSuccess)
        {
            return BadRequest(outcome.ValidationError);
        }

        var result = outcome.Pcp!;

        // PUT /pcp is its own primary event — not a sub-event of an UpdateMember
        // call — so its EventId comes from the request body (caller-supplied
        // idempotency key) or a fresh GUID if none was supplied.
        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = memberId,
            EventId = request.EventId ?? Guid.NewGuid().ToString(),
            EventType = MemberEventType.PcpChanged,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["providerId"] = request.ProviderId,
                ["providerNpi"] = request.ProviderNpi,
                ["effectiveDate"] = request.EffectiveDate.ToString("o"),
                ["reason"] = request.Reason,
                ["assignmentSource"] = request.AssignmentSource
            }
        }, ct);

        return Ok(result);
    }

    /// <summary>
    /// Full PCP assignment history for a member, newest first. Proxies through to
    /// coverage-service so portal / consent / audit stay on the member-service
    /// boundary.
    /// </summary>
    [HttpGet("{memberId}/pcp/history")]
    [ProducesResponseType(typeof(IReadOnlyList<PcpAssignmentHistoryItem>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetMemberPcpHistory([FromRoute] string memberId, CancellationToken ct)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        try
        {
            var history = await _coverage.GetPcpHistoryAsync(TenantId, memberId, ct);
            return Ok(history);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }
    }

    [HttpGet("{memberId}/coverage-history")]
    [ProducesResponseType(typeof(List<CoverageHistoryEvent>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetCoverageHistory([FromRoute] string memberId, CancellationToken ct)
    {
        try
        {
            var history = await _coverage.GetCoverageHistoryAsync(TenantId, memberId, ct);
            return Ok(history);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }
    }

    [HttpGet("{memberId}/834-transactions")]
    [ProducesResponseType(typeof(List<Enrollment834Record>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> Get834Transactions([FromRoute] string memberId, CancellationToken ct)
    {
        try
        {
            var txns = await _enrollment.Get834TransactionsAsync(TenantId, memberId, ct);
            return Ok(txns);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }
    }

    /// <summary>
    /// List per-member enrollment events. Proxies through to enrollment-import-service so
    /// consent scope, audit logging, tenant context, and rate limiting all live on this
    /// member-service boundary instead of being duplicated at every portal client.
    /// </summary>
    [HttpGet("{memberId}/enrollment-events")]
    [ProducesResponseType(typeof(EnrollmentEventListResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetEnrollmentEvents(
        [FromRoute] string memberId,
        [FromQuery] string? type = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? continuationToken = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memberId))
            return BadRequest(new { error = "memberId is required" });

        try
        {
            var page = await _enrollment.GetEnrollmentEventsAsync(
                TenantId, memberId, type, from, to, Math.Clamp(limit, 1, 200), continuationToken, ct);
            return Ok(page);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }
    }

    [HttpGet("{memberId}/accumulators")]
    [ProducesResponseType(typeof(MemberAccumulatorsResponse), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetAccumulators([FromRoute] string memberId, CancellationToken ct)
    {
        try
        {
            var acc = await _accumulators.GetAccumulatorsAsync(TenantId, memberId, ct);
            return Ok(acc);
        }
        catch (DownstreamUnavailableException ex)
        {
            return DownstreamUnavailable(ex);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }

    private IActionResult DownstreamUnavailable(DownstreamUnavailableException ex)
    {
        var problem = new ProblemDetails
        {
            Type = "https://cloudhealthoffice.com/problems/downstream-unavailable",
            Title = "Downstream service unavailable",
            Status = StatusCodes.Status503ServiceUnavailable,
            Detail = ex.Detail ?? ex.Message
        };
        problem.Extensions["service"] = ex.ServiceName;
        return StatusCode(StatusCodes.Status503ServiceUnavailable, problem);
    }

    private static JsonObject SnapshotPayload(Member member) => new()
    {
        ["id"] = member.Id,
        ["memberId"] = member.MemberId,
        ["tenantId"] = member.TenantId,
        ["groupNumber"] = member.GroupNumber,
        ["isSubscriber"] = member.IsSubscriber,
#pragma warning disable CS0618
        ["subscriberMemberId"] = member.SubscriberMemberId,
#pragma warning restore CS0618
        ["firstName"] = member.FirstName,
        ["lastName"] = member.LastName,
        ["middleName"] = member.MiddleName,
        ["dateOfBirth"] = member.DateOfBirth.ToString("yyyy-MM-dd"),
        ["gender"] = member.Gender,
        ["effectiveDate"] = member.EffectiveDate.ToString("o"),
        ["terminationDate"] = member.TerminationDate?.ToString("o"),
        ["status"] = member.Status.ToString(),
        ["lineOfBusiness"] = member.LineOfBusiness.ToString(),
        ["address"] = member.Address,
        ["city"] = member.City,
        ["state"] = member.State,
        ["zipCode"] = member.ZipCode,
        ["phone"] = member.Phone,
        ["email"] = member.Email,
        ["preferredLanguage"] = member.PreferredLanguage,
        ["birthSex"] = member.BirthSex,
        ["identifierCount"] = member.Identifiers.Count
    };
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
    public DateTime? PlanChangeEffectiveDate { get; set; }
    public decimal? MedicaidSpendDownLiabilityAmount { get; set; }
    public decimal MedicaidSpendDownAmountMet { get; set; }
    public EmploymentStatus? EmploymentStatus { get; set; }
    public bool? TobaccoUser { get; set; }
    public bool? IsStudent { get; set; }

    public string? PreferredLanguage { get; set; }
    public string? BirthSex { get; set; }

    /// <summary>Optional client-supplied idempotency key for the MemberCreated event.</summary>
    public string? EventId { get; set; }
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

    /// <summary>Optional idempotency key for the MemberUpdated event.</summary>
    public string? EventId { get; set; }
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

/// <summary>
/// Member-service projection of an enrollment-import-service event. Mirrors the
/// downstream document shape so the portal does not need to know about the underlying
/// service. Consent / audit / tenant filtering happen on this boundary.
/// </summary>
public class EnrollmentEventRecord
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime? EventDate { get; set; }
    public DateTime? RetroEffectiveDate { get; set; }
    public string? SourceBatchId { get; set; }
    public string? TransactionId { get; set; }
    public string? MaintenanceType { get; set; }
    public string? MaintenanceReason { get; set; }
    public string? Source { get; set; }
    public System.Text.Json.Nodes.JsonObject? Payload { get; set; }
    public string? RawSegment { get; set; }
}

public class EnrollmentEventListResponse
{
    public List<EnrollmentEventRecord> Items { get; set; } = new();
    public string? ContinuationToken { get; set; }
}

public class MemberAccumulatorsResponse
{
    public string MemberId { get; set; } = string.Empty;
    public DateTime PlanYearStart { get; set; }
    public DateTime PlanYearEnd { get; set; }

    public decimal IndividualDeductibleUsed { get; set; }
    public decimal IndividualDeductibleLimit { get; set; }
    public decimal FamilyDeductibleUsed { get; set; }
    public decimal FamilyDeductibleLimit { get; set; }
    public decimal IndividualOopUsed { get; set; }
    public decimal IndividualOopLimit { get; set; }
    public decimal FamilyOopUsed { get; set; }
    public decimal FamilyOopLimit { get; set; }

    public List<MemberServiceAccumulator> ServiceAccumulators { get; set; } = new();
    public List<MemberAccumulatorActivity> RecentActivity { get; set; } = new();
}

public class MemberServiceAccumulator
{
    public string BenefitCategory { get; set; } = string.Empty;
    public decimal Used { get; set; }
    public decimal Limit { get; set; }
    public string Unit { get; set; } = "USD";
}

public class MemberAccumulatorActivity
{
    public string EventId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string? SourceReference { get; set; }
    public DateTime OccurredAt { get; set; }
    public decimal DeductibleDelta { get; set; }
    public decimal OopDelta { get; set; }
    public decimal FamilyDeductibleDelta { get; set; }
    public decimal FamilyOopDelta { get; set; }
    public string? Reason { get; set; }
    public string ActorId { get; set; } = "system";
}

public class AssignPcpRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string? ProviderNpi { get; set; }
    public DateTime EffectiveDate { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Origin of the assignment: <c>MemberChoice</c> (default), <c>AutoAssigned</c>,
    /// or <c>AdminAssigned</c>. Forwarded to coverage-service and recorded on the
    /// PcpAssignment history row + PcpChanged event payload.
    /// </summary>
    public string? AssignmentSource { get; set; }

    /// <summary>
    /// Member DOB forwarded to coverage-service for age-range panel validation.
    /// Auto-populated from the member record when omitted.
    /// </summary>
    public DateTime? MemberDateOfBirth { get; set; }

    /// <summary>Optional idempotency key for the PcpChanged event.</summary>
    public string? EventId { get; set; }
}

public class TerminateMemberRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string CoverageId { get; set; } = string.Empty;
    public DateTime TerminationDate { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Notes { get; set; }

    /// <summary>Optional idempotency key for the MemberTerminated event.</summary>
    public string? EventId { get; set; }
}

#endregion
