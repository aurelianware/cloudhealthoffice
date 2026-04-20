using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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
/// Member notes (FHIR Communication). Notes are immutable — only Create + Read.
/// Corrections are new notes that link back to the original via
/// <see cref="CreateMemberNoteRequest.LinkedResourceType"/> = <c>"MemberNote"</c>.
/// </summary>
[ApiController]
[Route("api/v1/members/{memberId}/notes")]
public class MemberNotesController : ControllerBase
{
    private string TenantId => HttpContext.GetTenantId();

    private readonly IMemberRepository _members;
    private readonly IMemberNoteRepository _notes;
    private readonly IMemberEventPublisher _events;

    private readonly ILogger<MemberNotesController>? _logger;

    public MemberNotesController(
        IMemberRepository members,
        IMemberNoteRepository notes,
        IMemberEventPublisher events,
        ILogger<MemberNotesController>? logger = null)
    {
        _members = members;
        _notes = notes;
        _events = events;
        _logger = logger;
    }

    /// <summary>List notes for a member, newest first. Filter by category, paged.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MemberNoteListResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ListNotes(
        [FromRoute] string memberId,
        [FromQuery] MemberNoteCategory? category = null,
        [FromQuery][Range(1, 100)] int pageSize = 20,
        [FromQuery] string? continuationToken = null,
        CancellationToken ct = default)
    {
        var member = await _members.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var (items, token) = await _notes.ListByMemberAsync(
            TenantId, memberId, category, pageSize, continuationToken);

        await PublishViewedAsync(memberId,
            scope: category.HasValue ? $"list:{category.Value}" : "list:all",
            count: items.Count, ct);

        return Ok(new MemberNoteListResponse
        {
            Items = new List<MemberNote>(items),
            ContinuationToken = token
        });
    }

    /// <summary>Get a single note by id.</summary>
    [HttpGet("{noteId}")]
    [ProducesResponseType(typeof(MemberNote), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetNote(
        [FromRoute] string memberId,
        [FromRoute] string noteId,
        CancellationToken ct)
    {
        var note = await _notes.GetByIdAsync(TenantId, memberId, noteId);
        if (note == null) return NotFound();

        await PublishViewedAsync(memberId, scope: $"note:{noteId}", count: 1, ct);
        return Ok(note);
    }

    /// <summary>Create a new note. Notes are immutable once created.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(MemberNote), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CreateNote(
        [FromRoute] string memberId,
        [FromBody] CreateMemberNoteRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var member = await _members.GetByMemberIdAsync(TenantId, memberId);
        if (member == null) return NotFound();

        var actor = User.Identity?.Name ?? "System";
        var note = new MemberNote
        {
            TenantId = TenantId,
            MemberId = memberId,
            Category = request.Category,
            Subject = request.Subject,
            Body = request.Body,
            Author = string.IsNullOrEmpty(request.Author) ? actor : request.Author,
            LinkedResourceType = request.LinkedResourceType,
            LinkedResourceId = request.LinkedResourceId
        };

        var created = await _notes.CreateAsync(note);

        var eventId = string.IsNullOrEmpty(request.EventId)
            ? Guid.NewGuid().ToString()
            : request.EventId;

        await _events.PublishAsync(new MemberEvent
        {
            TenantId = TenantId,
            MemberId = memberId,
            EventId = eventId,
            EventType = MemberEventType.MemberNoteCreated,
            ActorId = actor,
            CorrelationId = HttpContext.TraceIdentifier,
            Payload = new JsonObject
            {
                ["noteId"] = created.Id,
                ["category"] = created.Category.ToString(),
                ["subject"] = created.Subject,
                ["author"] = created.Author,
                ["linkedResourceType"] = created.LinkedResourceType,
                ["linkedResourceId"] = created.LinkedResourceId
            }
        }, ct);

        return CreatedAtAction(nameof(GetNote),
            new { memberId, noteId = created.Id }, created);
    }

    private async Task PublishViewedAsync(string memberId, string scope, int count, CancellationToken ct)
    {
        // Best-effort audit; failures must not fail the read. See the
        // analogous comment on MemberAlertsController.PublishViewedAsync.
        try
        {
            await _events.PublishAsync(new MemberEvent
            {
                TenantId = TenantId,
                MemberId = memberId,
                EventId = Guid.NewGuid().ToString(),
                EventType = MemberEventType.MemberNoteViewed,
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
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Best-effort audit for MemberNoteViewed failed for {MemberId} scope={Scope}",
                memberId, scope);
        }
    }
}

public class CreateMemberNoteRequest
{
    [Required]
    public MemberNoteCategory Category { get; set; }

    [Required]
    [StringLength(200)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    [StringLength(8000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>If omitted, defaults to the authenticated user.</summary>
    [StringLength(200)]
    public string? Author { get; set; }

    [StringLength(50)]
    public string? LinkedResourceType { get; set; }

    [StringLength(100)]
    public string? LinkedResourceId { get; set; }

    public string? EventId { get; set; }
}

public class MemberNoteListResponse
{
    public List<MemberNote> Items { get; set; } = new();
    public string? ContinuationToken { get; set; }
}
