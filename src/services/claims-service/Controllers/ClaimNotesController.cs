using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace ClaimsService.Controllers;

/// <summary>
/// Notes a reviewer writes on a claim. Append-only: there is no edit or
/// delete. Reads need claims:read and writes claims:work (the service
/// defaults). The author and time are taken from the caller's token and the
/// server clock; the body carries only the category and text.
/// </summary>
[ApiController]
[Route("api/claims/{claimId}/notes")]
[Produces("application/json")]
public class ClaimNotesController : ControllerBase
{
    public const int MaxTextLength = 4000;

    private readonly IClaimRepository _claims;
    private readonly IClaimNoteRepository _notes;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ClaimNotesController> _logger;

    public ClaimNotesController(
        IClaimRepository claims,
        IClaimNoteRepository notes,
        ICurrentActor actor,
        ILogger<ClaimNotesController> logger)
    {
        _claims = claims;
        _notes = notes;
        _actor = actor;
        _logger = logger;
    }

    private string TenantId =>
        HttpContext?.Items["TenantId"]?.ToString() is { Length: > 0 } tenant ? tenant : _actor.TenantId;

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ClaimNote>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<ClaimNote>>> List(string claimId, CancellationToken ct = default)
    {
        return Ok(await _notes.ListByClaimAsync(claimId, TenantId, ct));
    }

    [HttpPost]
    [ProducesResponseType(typeof(ClaimNote), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClaimNote>> Add(
        string claimId, [FromBody] AddClaimNoteRequest request, CancellationToken ct = default)
    {
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return BadRequest("text is required");
        if (text.Length > MaxTextLength)
            return BadRequest($"text must be {MaxTextLength} characters or fewer");

        var category = ClaimNoteCategories.Normalize(request.Category);
        if (category is null)
            return BadRequest($"category must be one of: {string.Join(", ", ClaimNoteCategories.All)}");

        var claim = await _claims.GetByIdAsync(claimId);
        if (claim is null)
            return NotFound($"Claim {claimId} not found");

        var note = await _notes.AppendAsync(new ClaimNote
        {
            TenantId = TenantId,
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber,
            Category = category,
            Text = text,
            AuthorId = _actor.UserId,
            AuthorName = string.IsNullOrWhiteSpace(_actor.DisplayName) ? null : _actor.DisplayName,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        _logger.LogInformation("Note {NoteId} ({Category}) added to claim {ClaimId} by {AuthorId}",
            note.Id, note.Category, note.ClaimId, note.AuthorId);
        return Created($"api/claims/{claim.Id}/notes/{note.Id}", note);
    }
}
