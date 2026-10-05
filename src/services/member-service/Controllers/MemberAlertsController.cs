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
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MemberService.Controllers;

/// <summary>
/// Member alerts (FHIR Flag) — flags such as LitigationHold, CustodyDispute,
/// LanguageRequirement, etc. Alerts are end-dated rather than deleted; every
/// create / view / end emits a <see cref="MemberEvent"/> for audit.
/// </summary>
[ApiController]
[Route("api/v1/members/{memberId}/alerts")]
public class MemberAlertsController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IMemberRepository _members;
    private readonly IMemberAlertRepository _alerts;
    private readonly IMemberEventPublisher _events;
    private readonly IFhirFlagProjector _flagProjector;

    private readonly ILogger<MemberAlertsController>? _logger;

    public MemberAlertsController(
        IMemberRepository members,
        IMemberAlertRepository alerts,
        IMemberEventPublisher events,
        IFhirFlagProjector flagProjector,
        ILogger<MemberAlertsController>? logger = null)
    {
        _members = members;
        _alerts = alerts;
        _events = events;
        _flagProjector = flagProjector;
        _logger = logger;
    }

    /// <summary>List alerts for a member. Set <c>status=active</c> to filter to in-effect alerts.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MemberAlertListResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ListAlerts(
        [FromRoute] string memberId,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var member = await _members.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var activeOnly = string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
        var items = await _alerts.ListByMemberAsync(TenantId, memberId, activeOnly);

        await PublishViewedAsync(memberId, scope: activeOnly ? "list:active" : "list:all", count: items.Count, ct);

        return Ok(new MemberAlertListResponse { Items = items.ToList() });
    }

    /// <summary>Get a single alert by id.</summary>
    [HttpGet("{alertId}")]
    [ProducesResponseType(typeof(MemberAlert), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetAlert(
        [FromRoute] string memberId,
        [FromRoute] string alertId,
        CancellationToken ct)
    {
        var alert = await _alerts.GetByIdAsync(TenantId, memberId, alertId);
        if (alert == null) return NotFound();

        await PublishViewedAsync(memberId, scope: $"alert:{alertId}", count: 1, ct);
        return Ok(alert);
    }

    /// <summary>
    /// Writes to alerts admit either permission at the door; the alert type
    /// then decides (<see cref="RequiredPermissionFor"/>).
    /// </summary>
    private const string AlertWritePermissions = "members:write," + ChoRolePermissions.LegalHold;

    /// <summary>
    /// A LitigationHold alert places (or, ended, releases) a legal hold on the
    /// member: it needs records:legal-hold (ComplianceOfficer; TenantAdmin and
    /// PlatformAdmin through *:*), not members:write, so an enrollment or
    /// member-services user cannot place or lift one. Every other alert type
    /// needs members:write.
    /// </summary>
    internal static string RequiredPermissionFor(MemberAlertType type)
        => type == MemberAlertType.LitigationHold ? ChoRolePermissions.LegalHold : "members:write";

    private IActionResult? RefuseUnlessPermitted(MemberAlertType type, string action)
    {
        var required = RequiredPermissionFor(type);
        if (ChoPrincipal.HasPermission(User, required))
            return null;

        _logger?.LogWarning(
            "AUDIT member alert {Action} refused: {AlertType} needs {Permission} (subject {Subject}, tenant {TenantId})",
            action, type, required, User.FindFirst(ChoClaimTypes.Subject)?.Value, TenantId);
        return StatusCode(StatusCodes.Status403Forbidden,
            new { error = $"A {type} alert needs {required} to {action}." });
    }

    /// <summary>Create a new alert. A LitigationHold alert needs records:legal-hold; any other type members:write.</summary>
    [HttpPost]
    [RequirePermission(AlertWritePermissions)]
    [ProducesResponseType(typeof(MemberAlert), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CreateAlert(
        [FromRoute] string memberId,
        [FromBody] CreateMemberAlertRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        if (RefuseUnlessPermitted(request.AlertType, "create") is { } refused)
            return refused;

        var member = await _members.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var actor = User.Identity?.Name ?? "System";
        var alert = new MemberAlert
        {
            TenantId = TenantId,
            MemberId = memberId,
            AlertType = request.AlertType,
            Severity = request.Severity,
            StartDate = request.StartDate ?? DateTime.UtcNow,
            EndDate = request.EndDate,
            Reason = request.Reason,
            RequiredAction = request.RequiredAction,
            CreatedBy = actor
        };

        var created = await _alerts.CreateAsync(alert);

        var eventId = string.IsNullOrEmpty(request.EventId)
            ? Guid.NewGuid().ToString()
            : request.EventId;

        await _events.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = memberId,
            EventId = eventId,
            EventType = MemberEventType.MemberAlertCreated,
            ActorId = actor,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["alertId"] = created.Id,
                ["alertType"] = created.AlertType.ToString(),
                ["severity"] = created.Severity.ToString(),
                ["startDate"] = created.StartDate.ToString("o"),
                ["endDate"] = created.EndDate?.ToString("o"),
                ["reason"] = created.Reason,
                ["requiredAction"] = created.RequiredAction
            }
        }, ct);

        return CreatedAtAction(nameof(GetAlert),
            new { memberId, alertId = created.Id }, created);
    }

    /// <summary>
    /// End-date an alert. Idempotent: ending an already-ended alert is a no-op (200).
    /// Ending a LitigationHold alert releases the hold: records:legal-hold; any other type members:write.
    /// </summary>
    [HttpPost("{alertId}/end")]
    [RequirePermission(AlertWritePermissions)]
    [ProducesResponseType(typeof(MemberAlert), 200)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> EndAlert(
        [FromRoute] string memberId,
        [FromRoute] string alertId,
        [FromBody] EndMemberAlertRequest? request,
        CancellationToken ct)
    {
        var alert = await _alerts.GetByIdAsync(TenantId, memberId, alertId);
        if (alert == null) return NotFound();

        if (RefuseUnlessPermitted(alert.AlertType, "end") is { } refused)
            return refused;

        if (alert.EndDate.HasValue)
        {
            // Already ended — idempotent return.
            return Ok(alert);
        }

        var actor = User.Identity?.Name ?? "System";
        alert.EndDate = request?.EndDate ?? DateTime.UtcNow;
        alert.EndedBy = actor;

        var updated = await _alerts.EndAsync(alert);

        var eventId = string.IsNullOrEmpty(request?.EventId)
            ? Guid.NewGuid().ToString()
            : request!.EventId;

        await _events.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = memberId,
            EventId = eventId,
            EventType = MemberEventType.MemberAlertEnded,
            ActorId = actor,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["alertId"] = updated.Id,
                ["alertType"] = updated.AlertType.ToString(),
                ["endDate"] = updated.EndDate!.Value.ToString("o")
            }
        }, ct);

        return Ok(updated);
    }

    /// <summary>FHIR Flag projection. <c>?status=active</c> filters to in-effect alerts.</summary>
    [HttpGet("/api/v1/members/{memberId}/fhir/Flag")]
    [Produces("application/fhir+json", "application/json")]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetFhirFlags(
        [FromRoute] string memberId,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        var member = await _members.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var activeOnly = string.Equals(status, "active", StringComparison.OrdinalIgnoreCase);
        var items = await _alerts.ListByMemberAsync(TenantId, memberId, activeOnly);

        await PublishViewedAsync(memberId, scope: $"fhir:flag:{(activeOnly ? "active" : "all")}", count: items.Count, ct);

        var bundle = _flagProjector.ProjectBundle(items);
        return new ContentResult
        {
            ContentType = "application/fhir+json",
            Content = bundle.ToJsonString(),
            StatusCode = 200
        };
    }

    private async Task PublishViewedAsync(string memberId, string scope, int count, CancellationToken ct)
    {
        // Best-effort audit: a failure in the event publisher (downstream
        // Cosmos/Mongo hiccup, concurrency retries exhausted) must not fail
        // the read. The tradeoff is a potentially missing audit row; the
        // repository-level audit on Create/End is the primary integrity
        // guarantee, so missing Viewed rows degrade rather than break.
        try
        {
            await _events.PublishAsync(new MemberEvent
            {
                TenantId = TenantId,
                MemberId = memberId,
                EventId = Guid.NewGuid().ToString(),
                EventType = MemberEventType.MemberAlertViewed,
                ActorId = User.Identity?.Name ?? "System",
                CorrelationId = HttpContext.TraceIdentifier,
                Payload = new JsonObject
                {
                    ["scope"] = scope,
                    ["count"] = count
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled — propagate cleanly.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Best-effort audit for MemberAlertViewed failed for {MemberId} scope={Scope}",
                memberId, scope);
        }
    }
}

public class CreateMemberAlertRequest
{
    [Required]
    public MemberAlertType AlertType { get; set; }

    [Required]
    public MemberAlertSeverity Severity { get; set; }

    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    [Required]
    [StringLength(2000)]
    public string Reason { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? RequiredAction { get; set; }

    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class EndMemberAlertRequest
{
    /// <summary>End date. Defaults to now.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Optional client-supplied idempotency key for the audit event.</summary>
    public string? EventId { get; set; }
}

public class MemberAlertListResponse
{
    public List<MemberAlert> Items { get; set; } = new();
}
