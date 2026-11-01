using System.Text;
using CloudHealthOffice.Infrastructure.Security;
using EncounterSubmissionService.StateRoutes;
using Microsoft.AspNetCore.Mvc;

namespace EncounterSubmissionService.Controllers;

/// <summary>
/// State encounter routes for the caller's tenant: which states are ready, and the
/// 837 files each batch produced. The tenant always comes from the CHO token.
/// GET needs encounters:read, POST needs encounters:write (defaults set in Program.cs).
/// </summary>
[ApiController]
[Route("api/encounters/state")]
[Produces("application/json")]
public class StateEncounterRoutesController : ControllerBase
{
    private readonly IStateEncounterRouteRegistry _registry;
    private readonly IStateEncounterBatchService _batches;
    private readonly IStateResponseReconciler _responses;
    private readonly ICurrentActor _actor;

    public StateEncounterRoutesController(
        IStateEncounterRouteRegistry registry, IStateEncounterBatchService batches,
        IStateResponseReconciler responses, ICurrentActor actor)
    {
        _registry = registry;
        _batches = batches;
        _responses = responses;
        _actor = actor;
    }

    /// <summary>
    /// Readiness of every state route for this tenant, and what is missing where it is not ready.
    /// </summary>
    [HttpGet("routes")]
    [ProducesResponseType(typeof(IEnumerable<StateRouteStatus>), StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<StateRouteStatus>> GetRoutes()
    {
        var statuses = StateEncounterRoute.All.Select(route =>
        {
            var admitted = _registry.Resolve(route.StateCode, route.LineOfBusiness, _actor.TenantId, out var reason);
            var options = _registry.GetOptions(route.StateCode);
            return new StateRouteStatus
            {
                StateCode = route.StateCode,
                Receiver = route.Receiver,
                Format = route.Format.ToString(),
                Ready = admitted is not null,
                NotReadyReason = reason,
                UsageIndicator = route.Format == EncounterRouteFormat.X12_837 ? options?.UsageIndicator ?? "T" : null,
                DeadlineRule = (options?.DeadlineRule ?? route.DefaultDeadlineRule).ToString(),
                DeadlineDays = options?.DeadlineDays is > 0 ? options.DeadlineDays.Value : route.DefaultDeadlineDays,
                DeadlineSource = route.DeadlineSource,
                ResponseFiles = route.ResponseFiles
            };
        });
        return Ok(statuses);
    }

    /// <summary>Recent 837 files for this tenant, newest first, without their content.</summary>
    [HttpGet("files")]
    [ProducesResponseType(typeof(IEnumerable<StateEncounterFile>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<StateEncounterFile>>> ListFiles(
        [FromQuery] string? state = null, [FromQuery] int limit = 50) =>
        Ok(await _batches.ListFilesAsync(_actor.TenantId, state, limit));

    /// <summary>Downloads one 837 file exactly as it was written.</summary>
    [HttpGet("files/{fileId}/content")]
    [Produces("text/plain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadFile(string fileId)
    {
        var file = await _batches.GetFileAsync(_actor.TenantId, fileId);
        if (file is null) return NotFound();
        return File(Encoding.ASCII.GetBytes(file.Content), "text/plain", file.FileName);
    }

    /// <summary>
    /// Records that a staged file was handed to the state (SFTP drop, partner upload).
    /// Its batched encounters become Submitted.
    /// </summary>
    [HttpPost("files/{fileId}/transmitted")]
    [ProducesResponseType(typeof(StateEncounterFile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StateEncounterFile>> MarkTransmitted(string fileId, CancellationToken ct)
    {
        var file = await _batches.MarkTransmittedAsync(_actor.TenantId, fileId, ct);
        return file is null ? NotFound() : Ok(file);
    }

    /// <summary>
    /// Applies a response file from the state (999, 277CA / 277) to this tenant's
    /// encounters on that route, record by record. The DHCS EVR XML file is refused
    /// with 422 until its schema is implemented.
    /// </summary>
    [HttpPost("{stateCode}/responses")]
    [ProducesResponseType(typeof(StateResponseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StateResponseResult>> ApplyResponse(
        string stateCode, [FromBody] StateResponseRequest request, CancellationToken ct)
    {
        var route = _registry.Get(stateCode);
        if (route is null || route.Format != EncounterRouteFormat.X12_837)
            return BadRequest(new { message = $"'{stateCode}' has no X12 encounter route." });
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { message = "Content is required." });

        try
        {
            return Ok(await _responses.ApplyAsync(route, _actor.TenantId, request.FileName, request.Content, ct));
        }
        catch (NotSupportedException ex)
        {
            return UnprocessableEntity(new { message = ex.Message });
        }
        catch (FormatException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}

public sealed class StateResponseRequest
{
    public string? FileName { get; set; }
    public string Content { get; set; } = string.Empty;
}

public sealed class StateRouteStatus
{
    public string StateCode { get; set; } = string.Empty;
    public string Receiver { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public bool Ready { get; set; }
    public string? NotReadyReason { get; set; }
    public string? UsageIndicator { get; set; }
    public string DeadlineRule { get; set; } = string.Empty;
    public int DeadlineDays { get; set; }
    public string DeadlineSource { get; set; } = string.Empty;
    public IReadOnlyList<string> ResponseFiles { get; set; } = Array.Empty<string>();
}
