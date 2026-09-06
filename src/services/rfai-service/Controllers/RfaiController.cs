using Microsoft.AspNetCore.Mvc;
using RfaiService.Models;
using RfaiService.Repositories;
using RfaiService.Services;

namespace RfaiService.Controllers;

/// <summary>
/// Internal API over the RFAI case aggregate.
///
/// This is CHO's INTERNAL surface, deliberately behind the standards-facing one:
/// the Da Vinci CDex Task projection and the <c>$submit-attachment</c> operation
/// in fhir-service are what a provider or partner system talks to. Nothing here
/// is a substitute for that — a caller that reaches this API is another CHO
/// service (authorization-service raising a request, fhir-service projecting or
/// recording a response, attachment-service correlating a 275).
///
/// TENANCY. The tenant is always the one <c>TenantMiddleware</c> resolved from
/// the authenticated context or the gateway header. It is never taken from a
/// route segment or a request body: a body that names a tenant is data, not
/// authority.
/// </summary>
[ApiController]
[Route("api/rfai")]
[Produces("application/json")]
public class RfaiController : ControllerBase
{
    private readonly IRfaiRepository _repository;
    private readonly IRfaiCaseService _cases;
    private readonly ILogger<RfaiController> _logger;

    public RfaiController(
        IRfaiRepository repository,
        IRfaiCaseService cases,
        ILogger<RfaiController> logger)
    {
        _repository = repository;
        _cases = cases;
        _logger = logger;
    }

    private string TenantId =>
        HttpContext.Items["TenantId"]?.ToString() ?? "default-tenant";

    /// <summary>
    /// Create an additional-information request, idempotently.
    ///
    /// Repeating the call with the same <c>correlationKey</c> returns the case
    /// the first call created — the same document id is addressed either way, so
    /// a redelivered A4 review decision cannot open a second request. When a
    /// cycle for the authorization is already open, that cycle is returned
    /// rather than a new one.
    ///
    /// authNumber maps to TRN02 of the originating 278 transaction.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RfaiCase>> CreateCase([FromBody] CreateRfaiRequest request)
    {
        var creation = new RfaiCreationRequest
        {
            TenantId = TenantId,
            AuthNumber = request.AuthNumber ?? string.Empty,
            AuthorizationId = request.AuthorizationId,
            CorrelationKey = request.CorrelationKey,
            MemberId = request.MemberId,
            RequestingProviderNpi = request.RequestingProviderNpi,
            ReviewDecision = request.ReviewDecision,
            ReasonCode = request.ReasonCode,
            ReasonDescription = request.ReasonDescription,
            DueDate = request.DueDate,
            Notes = request.Notes,
            RequestedBy = request.RequestedBy,
            RequestSource = request.RequestSource ?? RfaiRequestSources.Unknown,
            RequestedItems = request.RequestedItems,
        };

        var validation = RfaiCaseLifecycle.Validate(creation);
        if (!validation.IsValid)
            return BadRequest(validation.Error);

        var result = await _cases.EnsureRequestAsync(creation, HttpContext.RequestAborted);

        // 200 rather than 201 when the call replayed onto an existing case: the
        // caller learns nothing was created, and can tell a replay from a first
        // delivery without comparing timestamps.
        return result.Created
            ? CreatedAtAction(nameof(GetCase), new { id = result.Case.Id }, result.Case)
            : Ok(result.Case);
    }

    /// <summary>
    /// Get an RFAI case by ID, within the caller's tenant.
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> GetCase(string id)
    {
        var rfaiCase = await _repository.GetByIdAsync(TenantId, id);

        if (rfaiCase == null)
            return NotFound($"RFAI case {id} not found.");

        return Ok(rfaiCase);
    }

    /// <summary>
    /// Get the case bearing a provider-facing tracking id (attachment control
    /// number). The correlation lookup a response path uses.
    /// </summary>
    [HttpGet("by-tracking/{trackingId}")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> GetByTracking(string trackingId)
    {
        if (!RfaiCaseLifecycle.IsSafeIdentifier(trackingId))
            return NotFound("RFAI case not found.");

        var rfaiCase = await _repository.GetByTrackingIdAsync(TenantId, trackingId);

        if (rfaiCase == null)
            return NotFound("RFAI case not found.");

        return Ok(rfaiCase);
    }

    /// <summary>
    /// Get all RFAI cases for a given authorization number, newest first.
    /// Closed and cancelled cycles are included: the history of what was asked
    /// for and what came back is evidence, not clutter.
    /// </summary>
    [HttpGet("by-auth/{authNumber}")]
    [ProducesResponseType(typeof(IEnumerable<RfaiCase>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<RfaiCase>>> GetByAuth(string authNumber)
    {
        var cases = await _repository.GetByAuthNumberAsync(TenantId, authNumber);
        return Ok(cases);
    }

    /// <summary>
    /// Legacy route that carried the tenant in the path. The path tenant is now
    /// only honoured when it MATCHES the authenticated one — it never selects a
    /// tenant. Retained so existing callers keep working while they migrate to
    /// <c>GET by-auth/{authNumber}</c>.
    /// </summary>
    [HttpGet("by-auth/{tenantId}/{authNumber}")]
    [ProducesResponseType(typeof(IEnumerable<RfaiCase>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<RfaiCase>>> GetByAuthLegacy(
        string tenantId, string authNumber)
    {
        if (!string.Equals(tenantId, TenantId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "RFAI by-auth refused: path tenant does not match the authenticated tenant");
            return StatusCode(StatusCodes.Status403Forbidden,
                "The tenant in the path does not match the authenticated tenant.");
        }

        var cases = await _repository.GetByAuthNumberAsync(TenantId, authNumber);
        return Ok(cases);
    }

    /// <summary>
    /// Record that the request has been handed to the provider/system. Kept as
    /// an explicit action rather than a side effect of reading, so provenance
    /// says "delivered", not merely "someone looked".
    /// </summary>
    [HttpPost("{id}/delivered")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RfaiCase>> MarkDelivered(string id)
    {
        var updated = await _cases.MarkDeliveredAsync(TenantId, id, HttpContext.RequestAborted);
        if (updated == null)
            return NotFound($"RFAI case {id} not found.");

        return Ok(updated);
    }

    /// <summary>
    /// Record a response against this case.
    ///
    /// Idempotent by <c>submissionId</c>: replaying a submission records nothing,
    /// changes no status, and re-announces nothing. A NEW artifact under the same
    /// request is appended as an additional response — it never overwrites an
    /// earlier one.
    /// </summary>
    [HttpPost("{id}/responses")]
    [ProducesResponseType(typeof(RfaiResponseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RfaiResponseResult>> RecordResponse(
        string id, [FromBody] RecordRfaiResponseRequest request)
    {
        if (request.Artifacts.Count == 0)
            return BadRequest("At least one artifact is required.");

        if (request.Artifacts.Any(a => string.IsNullOrWhiteSpace(a.SubmissionId)))
            return BadRequest("Each artifact must carry a submissionId.");

        var result = await _cases.RecordResponseAsync(
            TenantId, id, request.Artifacts, HttpContext.RequestAborted);

        if (result is null)
            return NotFound($"RFAI case {id} not found.");

        // A refusal answers in the SAME shape as an acceptance, carrying the
        // outcome name. A plain-string 409 forced every caller to collapse
        // "closed" and "at capacity" into one guess — and the CDex surface then
        // reported a capacity refusal as "no longer open".
        if (result.IsRefusal)
        {
            return Conflict(new RfaiResponseResult
            {
                Outcome = result.Outcome.ToString(),
                Recorded = 0,
                ResumedReview = false,
                Detail = result.Outcome == RfaiIntakeOutcome.TooManyArtifacts
                    ? $"RFAI case {id} would exceed the maximum of "
                      + $"{RfaiCaseLifecycle.MaxArtifactsPerCase} artifacts."
                    : $"RFAI case {id} is {result.Case.Status} and cannot take a response.",
            });
        }

        return Ok(new RfaiResponseResult
        {
            Outcome = result.Outcome.ToString(),
            Recorded = result.Recorded.Count,
            ResumedReview = result.TransitionedToDocsReceived,
            Case = result.Case,
        });
    }

    /// <summary>
    /// Mark an attachment as received for this RFAI case.
    /// Transitions status Open → DocsReceived.
    /// Called by attachment-service once a 275 attachment is correlated.
    ///
    /// Retained at its original route and shape; it now funnels through the same
    /// intake as every other response path, so a redelivered 275 is recognised as
    /// a duplicate instead of appending a second copy.
    /// </summary>
    [HttpPost("{id}/attachments/received")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RfaiCase>> AttachmentReceived(
        string id,
        [FromBody] AttachmentReceivedRequest request)
    {
        // A 275 identifies itself by its attachment control number; where the
        // sender gave none, the content hash identifies the submission. Either
        // way the id is derived, not invented per call, so a redelivery matches.
        var submissionId = request.SubmissionId
            ?? request.AttachmentControlNumber
            ?? request.FileHash
            ?? RfaiCaseLifecycle.Sha256Hex(
                $"{TenantId}|{id}|{request.StorageKey}|{request.ReceivedAt:O}")[..32];

        var artifact = new RfaiResponseArtifact
        {
            SubmissionId = submissionId,
            ReceivedAt = request.ReceivedAt,
            AttachmentControlNumber = request.AttachmentControlNumber,
            StorageProvider = request.StorageProvider,
            StorageKey = request.StorageKey,
            FileHash = request.FileHash,
            SourceTransaction = request.SourceTransaction,
            Channel = RfaiResponseChannels.X12Attachment275,
        };

        var result = await _cases.RecordResponseAsync(
            TenantId, id, [artifact], HttpContext.RequestAborted);

        if (result is null)
            return NotFound($"RFAI case {id} not found.");

        if (result.IsRefusal)
        {
            return Conflict(result.Outcome == RfaiIntakeOutcome.TooManyArtifacts
                ? $"RFAI case {id} would exceed the maximum of "
                  + $"{RfaiCaseLifecycle.MaxArtifactsPerCase} artifacts."
                : $"RFAI case {id} is {result.Case.Status} and cannot take a response.");
        }

        return Ok(result.Case);
    }

    /// <summary>Close a case — the payer is done with this cycle.</summary>
    [HttpPost("{id}/close")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RfaiCase>> Close(string id, [FromBody] CloseRfaiRequest? request)
        => TransitionAsync(id, request, RfaiCaseLifecycle.Close, "closed");

    /// <summary>Cancel a case — the request is withdrawn.</summary>
    [HttpPost("{id}/cancel")]
    [ProducesResponseType(typeof(RfaiCase), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<RfaiCase>> Cancel(string id, [FromBody] CloseRfaiRequest? request)
        => TransitionAsync(id, request, RfaiCaseLifecycle.Cancel, "cancelled");

    private async Task<ActionResult<RfaiCase>> TransitionAsync(
        string id,
        CloseRfaiRequest? request,
        Func<RfaiCase, string?, string?, DateTime, bool> transition,
        string verb)
    {
        var rfaiCase = await _repository.GetByIdAsync(TenantId, id);
        if (rfaiCase == null)
            return NotFound($"RFAI case {id} not found.");

        if (!transition(rfaiCase, request?.By, request?.Reason, DateTime.UtcNow))
            return Conflict($"RFAI case {id} is already {rfaiCase.Status}.");

        var updated = await _repository.UpdateAsync(rfaiCase);

        _logger.LogInformation("RFAI case {Id} {Verb}", SanitizeForLog(id), verb);

        return Ok(updated);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

// ── Request / response DTOs ───────────────────────────────────────────────────

public class CreateRfaiRequest
{
    public string? AuthNumber { get; set; }
    public string? AuthorizationId { get; set; }

    /// <summary>
    /// Identity of the event asking for this request (typically a digest of the
    /// A4 review decision). Supply it to make creation replay-safe.
    /// </summary>
    public string? CorrelationKey { get; set; }

    public string? MemberId { get; set; }
    public string? RequestingProviderNpi { get; set; }
    public string? ReviewDecision { get; set; }
    public string? ReasonCode { get; set; }
    public string? ReasonDescription { get; set; }
    public string? RequestedBy { get; set; }
    public string? RequestSource { get; set; }
    public DateTime? DueDate { get; set; }
    public List<RequestedItem> RequestedItems { get; set; } = new();
    public string? Notes { get; set; }
}

public class AttachmentReceivedRequest
{
    public string? SubmissionId { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public string? AttachmentControlNumber { get; set; }
    public string? StorageProvider { get; set; }
    public string? StorageKey { get; set; }
    public string? FileHash { get; set; }
    public SourceTransaction? SourceTransaction { get; set; }
}

public class RecordRfaiResponseRequest
{
    public List<RfaiResponseArtifact> Artifacts { get; set; } = new();
}

public class RfaiResponseResult
{
    /// <summary>
    /// The intake outcome by name — <c>Accepted</c>, <c>DuplicateIgnored</c>,
    /// <c>CaseNotOpenForResponse</c> or <c>TooManyArtifacts</c>. Present on a
    /// refusal as well as on success, so a caller never has to infer which
    /// conflict it hit from the status code alone.
    /// </summary>
    public string Outcome { get; set; } = string.Empty;

    public int Recorded { get; set; }

    /// <summary>True when this call is the one that lets the authorization resume review.</summary>
    public bool ResumedReview { get; set; }

    /// <summary>Operator-facing explanation of a refusal. Never present on success.</summary>
    public string? Detail { get; set; }

    public RfaiCase? Case { get; set; }
}

public class CloseRfaiRequest
{
    public string? By { get; set; }
    public string? Reason { get; set; }
}
