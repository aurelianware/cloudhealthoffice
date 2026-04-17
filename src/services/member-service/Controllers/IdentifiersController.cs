using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using MemberService.Middleware;
using MemberService.Models;
using MemberService.Repositories;
using MemberService.Services;
using Microsoft.AspNetCore.Mvc;

namespace MemberService.Controllers;

/// <summary>
/// Typed identifier management (Medicaid, MBI, Medicare, Exchange, Portal, Legacy).
///
/// PII identifiers (SSN, MBI, Medicaid) are encrypted-at-rest via
/// <see cref="IIdentifierEncryptor"/> and are returned redacted when listed.
/// </summary>
[ApiController]
[Route("api/v1/members/{memberId}/identifiers")]
public class IdentifiersController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IMemberRepository _memberRepository;
    private readonly IIdentifierEncryptor _encryptor;
    private readonly IMemberEventPublisher _eventPublisher;

    public IdentifiersController(
        IMemberRepository memberRepository,
        IIdentifierEncryptor encryptor,
        IMemberEventPublisher eventPublisher)
    {
        _memberRepository = memberRepository;
        _encryptor = encryptor;
        _eventPublisher = eventPublisher;
    }

    /// <summary>List typed identifiers for a member. PII values are redacted.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<IdentifierResponse>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> List([FromRoute] string memberId)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var response = member.Identifiers
            .Select(i => new IdentifierResponse
            {
                Type = i.Type,
                System = i.System,
                Value = i.IsEncrypted ? "[REDACTED]" : i.Value,
                Use = i.Use,
                PeriodStart = i.PeriodStart,
                PeriodEnd = i.PeriodEnd,
                Assigner = i.Assigner,
                IsEncrypted = i.IsEncrypted
            })
            .ToList();
        return Ok(response);
    }

    /// <summary>Add a typed identifier to a member. Idempotent on (system, value).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(IdentifierResponse), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(409)]
    public async Task<IActionResult> Add(
        [FromRoute] string memberId,
        [FromBody] AddIdentifierRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var system = ResolveSystem(request);

        var isPii = IsPii(request.Type);
        var storedValue = isPii
            ? await _encryptor.EncryptAsync(request.Value, ct) ?? request.Value
            : request.Value;

        if (member.Identifiers.Any(i => i.System == system && i.Value == storedValue))
            return Conflict(new { message = "Identifier already exists on this member." });

        var identifier = new MemberIdentifier
        {
            Type = request.Type,
            System = system,
            Value = storedValue,
            Use = string.IsNullOrEmpty(request.Use) ? "official" : request.Use,
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            Assigner = request.Assigner,
            IsEncrypted = isPii && _encryptor.IsEnabled
        };
        member.Identifiers.Add(identifier);
        member.LastUpdatedDate = DateTime.UtcNow;
        member.LastUpdatedBy = User.Identity?.Name ?? "System";

        await _memberRepository.UpdateAsync(member);

        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = memberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = MemberEventType.MemberUpdated,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["identifierAdded"] = new JsonObject
                {
                    ["type"] = identifier.Type.ToString(),
                    ["system"] = identifier.System
                }
            }
        }, ct);

        return CreatedAtAction(nameof(List), new { memberId }, new IdentifierResponse
        {
            Type = identifier.Type,
            System = identifier.System,
            Value = identifier.IsEncrypted ? "[REDACTED]" : identifier.Value,
            Use = identifier.Use,
            PeriodStart = identifier.PeriodStart,
            PeriodEnd = identifier.PeriodEnd,
            Assigner = identifier.Assigner,
            IsEncrypted = identifier.IsEncrypted
        });
    }

    /// <summary>Remove an identifier by (system, value) — value must be plaintext for non-PII.</summary>
    [HttpDelete]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> Remove(
        [FromRoute] string memberId,
        [FromQuery, Required] string system,
        [FromQuery, Required] string value,
        CancellationToken ct)
    {
        var member = await _memberRepository.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var removed = 0;
        var kept = new List<MemberIdentifier>(member.Identifiers.Count);
        foreach (var i in member.Identifiers)
        {
            if (i.System != system) { kept.Add(i); continue; }

            var plain = i.IsEncrypted
                ? await _encryptor.DecryptAsync(i.Value, ct)
                : i.Value;
            if (plain == value) { removed++; continue; }
            kept.Add(i);
        }

        if (removed == 0) return NotFound();

        member.Identifiers = kept;
        member.LastUpdatedDate = DateTime.UtcNow;
        member.LastUpdatedBy = User.Identity?.Name ?? "System";
        await _memberRepository.UpdateAsync(member);

        await _eventPublisher.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = memberId,
            EventId = Guid.NewGuid().ToString(),
            EventType = MemberEventType.MemberUpdated,
            ActorId = User.Identity?.Name,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["identifierRemoved"] = new JsonObject
                {
                    ["system"] = system
                }
            }
        }, ct);

        return NoContent();
    }

    private static string ResolveSystem(AddIdentifierRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.System)) return request.System!;
        if (request.Type == MemberIdentifierType.Legacy)
        {
            if (string.IsNullOrWhiteSpace(request.LegacySlug))
                throw new ValidationException(
                    "LegacySlug is required when Type=Legacy and System is not explicitly provided.");
            return FhirIdentifierSystems.LegacyForSystem(request.LegacySlug);
        }
        return FhirIdentifierSystems.FromType(request.Type);
    }

    private static bool IsPii(MemberIdentifierType type) => type switch
    {
        MemberIdentifierType.SSN => true,
        MemberIdentifierType.MedicareMbi => true,
        MemberIdentifierType.Medicaid => true,
        _ => false
    };
}

public class AddIdentifierRequest
{
    [Required]
    public MemberIdentifierType Type { get; set; }

    [Required]
    [StringLength(512)]
    public string Value { get; set; } = string.Empty;

    /// <summary>Optional explicit system URI. If omitted, derived from <see cref="Type"/>.</summary>
    [StringLength(256)]
    public string? System { get; set; }

    /// <summary>Required when <see cref="Type"/>=<see cref="MemberIdentifierType.Legacy"/> and <see cref="System"/> is omitted.</summary>
    [StringLength(64)]
    public string? LegacySlug { get; set; }

    [StringLength(16)]
    public string? Use { get; set; }

    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }

    [StringLength(200)]
    public string? Assigner { get; set; }
}

public class IdentifierResponse
{
    public MemberIdentifierType Type { get; set; }
    public string System { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Use { get; set; } = "official";
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public string? Assigner { get; set; }
    public bool IsEncrypted { get; set; }
}
