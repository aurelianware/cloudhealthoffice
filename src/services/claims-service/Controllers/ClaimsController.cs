using ClaimsService.Audit;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services;
using ClaimsService.Services.Adjudication;
using ClaimsService.Models.Adjudication;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace ClaimsService.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class ClaimsController : ControllerBase
{
    private readonly IClaimRepository _claimRepository;
    private readonly IMassAdjudicationRunRepository _massAdjudicationRunRepository;
    private readonly IAiExaminationAuditRepository _auditRepository;
    private readonly IClaimAcknowledgmentService _ackService;
    private readonly IClaimVersionEventReader _versionEventReader;
    private readonly IClaimSubmissionService _submissionService;
    private readonly IClaimFinalizationService _finalizationService;
    private readonly IClaimDiagnosisMetadataEnricher _diagnosisMetadataEnricher;
    private readonly IClaimResolutionService _resolutionService;
    private readonly IClaimStatusTransitionService _statusTransitionService;
    private readonly IClaimVoidService _voidService;
    private readonly IConfiguration _configuration;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ClaimsController> _logger;

    /// <summary>The constructor dependency injection uses.</summary>
    [ActivatorUtilitiesConstructor]
    public ClaimsController(
        IClaimRepository claimRepository,
        IMassAdjudicationRunRepository massAdjudicationRunRepository,
        IAiExaminationAuditRepository auditRepository,
        IClaimAcknowledgmentService ackService,
        IClaimVersionEventReader versionEventReader,
        IClaimSubmissionService submissionService,
        IClaimFinalizationService finalizationService,
        IClaimDiagnosisMetadataEnricher diagnosisMetadataEnricher,
        IClaimResolutionService resolutionService,
        IClaimStatusTransitionService statusTransitionService,
        IClaimVoidService voidService,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger<ClaimsController> logger)
    {
        _claimRepository = claimRepository;
        _massAdjudicationRunRepository = massAdjudicationRunRepository;
        _auditRepository = auditRepository;
        _ackService = ackService;
        _versionEventReader = versionEventReader;
        _submissionService = submissionService;
        _finalizationService = finalizationService;
        _diagnosisMetadataEnricher = diagnosisMetadataEnricher;
        _resolutionService = resolutionService;
        _statusTransitionService = statusTransitionService;
        _voidService = voidService;
        _configuration = configuration;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>
    /// Direct construction from the underlying dependencies (tests build the
    /// controller this way): builds the resolution, status-transition and
    /// void services over them, logging through <paramref name="logger"/>
    /// exactly as the controller did. <paramref name="benefitEngine"/> is
    /// accepted for source compatibility and unused, as before.
    /// </summary>
    public ClaimsController(
        IClaimRepository claimRepository,
        IMassAdjudicationRunRepository massAdjudicationRunRepository,
        IAiExaminationAuditRepository auditRepository,
        IClaimAcknowledgmentService ackService,
        IClaimEventPublisher eventPublisher,
        IClaimVersionEventPublisher versionEventPublisher,
        IClaimVersionEventReader versionEventReader,
        IClaimSubmissionService submissionService,
        IClaimFinalizationService finalizationService,
        IClaimDiagnosisMetadataEnricher diagnosisMetadataEnricher,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger<ClaimsController> logger,
        IClaimApprovalReadjudicator? approvalReadjudicator = null,
        CloudHealthOffice.BenefitEngine.Services.IBenefitCalculationEngine? benefitEngine = null,
        IAccumulatorOutboxProcessor? accumulatorOutbox = null,
        IAccumulatorCommitLookup? accumulatorCommitLookup = null,
        IClaimWorkEventPublisher? workEvents = null,
        EditOverridePolicy? editOverridePolicy = null,
        IClaimNoteRepository? claimNotes = null)
        : this(
            claimRepository, massAdjudicationRunRepository, auditRepository, ackService, versionEventReader,
            submissionService, finalizationService, diagnosisMetadataEnricher,
            new ClaimResolutionService(
                claimRepository, auditRepository, eventPublisher, versionEventPublisher, configuration, actor, logger,
                approvalReadjudicator, accumulatorOutbox, accumulatorCommitLookup, workEvents, editOverridePolicy, claimNotes),
            new ClaimVoidService(finalizationService, actor, logger),
            claimRepository, eventPublisher, configuration, actor, logger)
    {
    }

    private ClaimsController(
        IClaimRepository claimRepository,
        IMassAdjudicationRunRepository massAdjudicationRunRepository,
        IAiExaminationAuditRepository auditRepository,
        IClaimAcknowledgmentService ackService,
        IClaimVersionEventReader versionEventReader,
        IClaimSubmissionService submissionService,
        IClaimFinalizationService finalizationService,
        IClaimDiagnosisMetadataEnricher diagnosisMetadataEnricher,
        ClaimResolutionService resolutionService,
        ClaimVoidService voidService,
        IClaimRepository statusClaimRepository,
        IClaimEventPublisher eventPublisher,
        IConfiguration configuration,
        ICurrentActor actor,
        ILogger<ClaimsController> logger)
        : this(
            claimRepository, massAdjudicationRunRepository, auditRepository, ackService, versionEventReader,
            submissionService, finalizationService, diagnosisMetadataEnricher,
            resolutionService,
            new ClaimStatusTransitionService(statusClaimRepository, eventPublisher, voidService, (ILogger)logger),
            voidService,
            configuration, actor, logger)
    {
    }

    /// <summary>
    /// Returns the tenant ID or throws if missing. Use on mutation endpoints
    /// where empty tenant would break multi-tenant isolation.
    /// </summary>
    private string GetTenantId()
    {
        var tenantId = HttpContext?.Items["TenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new InvalidOperationException(
                "TenantId not found in HttpContext. Ensure tenant middleware is configured.");
        }
        return tenantId;
    }

    /// <summary>
    /// Returns the tenant ID or empty string. Use on read-only endpoints
    /// where degraded operation is acceptable.
    /// </summary>
    private string TryGetTenantId() =>
        HttpContext?.Items["TenantId"]?.ToString() ?? string.Empty;

    /// <summary>
    /// Submit new claim (837 transaction). Deprecated — use
    /// <c>POST /api/v1/claims</c> which accepts <c>AdapterClaim</c>
    /// and emits <c>ClaimVersionSubmitted</c> events. Internally
    /// routes through <see cref="IClaimSubmissionService"/> so the
    /// audit chain stays continuous until capability 5.13 removes
    /// this endpoint.
    /// </summary>
    [HttpPost]
    [Obsolete("Use POST /api/v1/claims instead. The canonical V1 surface accepts AdapterClaim DTOs " +
              "and emits ClaimVersionSubmitted events. Legacy POST /api/claims will be removed in " +
              "capability 5.13 (Phase 1 closer).")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> SubmitClaim([FromBody] Claim claim, CancellationToken ct = default)
    {
        // RFC 8594 deprecation signal on every response from the legacy
        // submission path. Sunset is intentionally omitted — capability
        // 5.13 sets it when removal actually schedules.
        Response.Headers["Deprecation"] = "true";
        Response.Headers["Link"] = "</api/v1/claims>; rel=\"successor-version\"";

        _logger.LogInformation(
            "Legacy claim submission for member {MemberId}, provider {ProviderNPI}, service date {ServiceDate}",
            LogSanitizer.MaskId(claim.MemberId), LogSanitizer.Sanitize(claim.BillingProviderNPI), claim.ServiceDateFrom);

        var tenantId = GetTenantId();
        var actorId = ResolveActorId();
        var correlationId = ResolveCorrelationId();

        // Map domain Claim → AdapterClaim. The 5.2 round-trip mapper is
        // loss-less per SubmitClaimAsync_round_trips_AdapterClaim_losslessly,
        // so the canonical submission service can do its work and we map
        // the response back to domain shape for the legacy 201 contract.
        // The body is a full domain Claim; lifecycle, adjudication and audit
        // fields on it are server-owned and never taken from the caller.
        var submission = AdapterClaim.From(claim);
        ClaimSubmissionInput.ResetServerOwnedFields(submission, actorId);

        var result = await _submissionService.SubmitAsync(
            submission, tenantId, actorId, correlationId, ct);

        if (!result.Success)
        {
            return MapSubmissionFailure(result);
        }

        var created = result.Claim!.ToClaim();
        _logger.LogInformation(
            "Claim {ClaimNumber} submitted successfully (legacy path)",
            LogSanitizer.Sanitize(created.ClaimNumber));

        return CreatedAtAction(nameof(GetClaimById), new { id = created.Id }, created);
    }

    private IActionResult MapSubmissionFailure(ClaimSubmissionResult result)
    {
        var errors = result.Errors.Select(e => new
        {
            field = e.Field,
            code = e.Code,
            message = e.Message
        });

        return result.FailureKind switch
        {
            ClaimSubmissionFailureKind.NotImplemented => StatusCode(
                StatusCodes.Status501NotImplemented,
                new
                {
                    error = "Claim submission is not implemented for this tenant's configured platform",
                    errors
                }),
            _ => BadRequest(new
            {
                error = "Claim submission validation failed",
                errors
            }),
        };
    }

    /// <summary>The acting user or service, from the validated token only.</summary>
    private string ResolveActorId() => _actor.UserId;

    private string? ResolveCorrelationId()
    {
        if (HttpContext.Request.Headers.TryGetValue("X-Correlation-Id", out var header) &&
            !string.IsNullOrEmpty(header.ToString()))
        {
            return header.ToString();
        }
        return Activity.Current?.Id;
    }

    /// <summary>
    /// Get recent claims (for dashboard display)
    /// </summary>
    [HttpGet("recent")]
    [AuditClaimAccess(ClaimAccessActions.List)]
    [ProducesResponseType(typeof(IEnumerable<Claim>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<Claim>>> GetRecentClaims(
        [FromQuery][Range(1, 100)] int count = 10)
    {
        _logger.LogInformation("Fetching {Count} recent claims", count);

        var claims = await _claimRepository.SearchAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: null, lineOfBusiness: null,
            page: 1, pageSize: count);

        await _diagnosisMetadataEnricher.EnrichAsync(claims);
        return Ok(claims);
    }

    /// <summary>
    /// Get claim by ID
    /// </summary>
    [HttpGet("{id}")]
    [AuditClaimAccess(ClaimAccessActions.View, ClaimIdRouteValue = "id")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> GetClaimById(string id)
    {
        _logger.LogInformation("Fetching claim by ID: {Id}", LogSanitizer.Sanitize(id));

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        await _diagnosisMetadataEnricher.EnrichAsync(claim);
        return Ok(claim);
    }

    /// <summary>
    /// Returns the tenant-scoped, chronological audit history for a claim.
    /// The read model combines the append-only version stream with the
    /// structured pend snapshot, which is intentionally not a version event.
    /// </summary>
    [HttpGet("{id}/audit-timeline")]
    [AuditClaimAccess(ClaimAccessActions.AuditTimeline, ClaimIdRouteValue = "id")]
    [ProducesResponseType(typeof(IReadOnlyList<ClaimAuditTimelineEntry>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ClaimAuditTimelineEntry>>> GetAuditTimeline(
        string id,
        CancellationToken ct)
    {
        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null) return NotFound($"Claim {id} not found");

        var events = string.IsNullOrWhiteSpace(claim.ClaimVersionId)
            ? Array.Empty<ClaimVersionEvent>()
            : await _versionEventReader.GetAsync(GetTenantId(), claim.ClaimVersionId, ct);

        var timeline = events
            .Where(evt => claim.PendDetails is null
                          || evt.EventType != ClaimVersionEventType.ClaimVersionAdjudicated
                          || evt.VersionId != claim.Id)
            .Select(MapAuditEvent)
            .ToList();
        if (claim.PendDetails is not null)
        {
            timeline.Add(new ClaimAuditTimelineEntry
            {
                Timestamp = claim.PendDetails.PendedAt,
                Action = "Claim pended for review",
                ChangedBy = "adjudication-engine",
                OldValue = "Submitted",
                NewValue = "Pended",
                Notes = string.IsNullOrWhiteSpace(claim.PendDetails.PendReason)
                    ? claim.PendDetails.PendCode
                    : $"{claim.PendDetails.PendCode}: {claim.PendDetails.PendReason}"
            });

        }

        return Ok(timeline.OrderBy(entry => entry.Timestamp).ToList());
    }

    private static ClaimAuditTimelineEntry MapAuditEvent(ClaimVersionEvent evt)
    {
        var (action, oldValue, newValue) = evt.EventType switch
        {
            ClaimVersionEventType.ClaimVersionSubmitted => ("Claim submitted", (string?)null, "Submitted"),
            ClaimVersionEventType.ClaimVersionAdjudicated => ("Claim adjudicated", "Submitted", "Approved"),
            ClaimVersionEventType.ClaimVersionPaid => ("Payment recorded", "Approved", "Paid"),
            // Emitted when the payment cycle finalizes a Deny (835 sent).
            ClaimVersionEventType.ClaimVersionDenied => ("Denial finalized (835 sent)", "Deny", "Denied"),
            ClaimVersionEventType.ClaimVersionSuperseded => ("Claim version superseded", (string?)null, "Superseded"),
            ClaimVersionEventType.ClaimVersionVoided => ("Claim voided", (string?)null, "Voided"),
            ClaimVersionEventType.ClaimVersionReversed => ("Claim reversed", (string?)null, "Reversed"),
            ClaimVersionEventType.ClaimVersionResolved => (
                "Examiner disposition recorded",
                "Pended",
                evt.Payload?["disposition"]?.ToString()),
            _ => (evt.EventType.ToString(), (string?)null, (string?)null)
        };

        var reason = evt.Payload?["reason"]?.ToString()
            ?? evt.Payload?["adjustmentReason"]?.ToString()
            ?? evt.Payload?["denialReasonCode"]?.ToString()
            ?? evt.Payload?["notes"]?.ToString();

        return new ClaimAuditTimelineEntry
        {
            Timestamp = evt.OccurredAt,
            Action = action,
            ChangedBy = string.IsNullOrWhiteSpace(evt.ActorId) ? "system" : evt.ActorId,
            OldValue = oldValue,
            NewValue = newValue,
            Notes = reason,
            EventType = evt.EventType.ToString(),
            Version = evt.Version,
            CorrelationId = evt.CorrelationId
        };
    }

    /// <summary>
    /// Get a persisted adjudication projection for claim detail transparency.
    /// </summary>
    [HttpGet("{id}/adjudication-detail")]
    [AuditClaimAccess(ClaimAccessActions.AdjudicationDetail, ClaimIdRouteValue = "id")]
    [ProducesResponseType(typeof(AdjudicationTransparencyData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdjudicationTransparencyData>> GetAdjudicationDetail(string id)
    {
        _logger.LogInformation("Fetching adjudication detail for claim ID: {Id}", LogSanitizer.Sanitize(id));

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        var detail = AdjudicationTransparencyBuilder.Build(claim);
        if (detail is null)
        {
            return NotFound($"Claim {id} has no persisted adjudication detail");
        }

        return Ok(detail);
    }

    /// <summary>
    /// Prior claims the duplicate-claim stage matched this claim against
    /// (pend code DUPLICATE), one entry per matched claim with its claim
    /// number, service dates, billed amount, status, match type and the
    /// fields that matched. Empty when the claim has no duplicate findings.
    /// Matched claims are read through the tenant-scoped repository, so a
    /// claim outside the caller's tenant is reported as not found rather
    /// than summarized.
    /// </summary>
    [HttpGet("{id}/duplicate-matches")]
    [AuditClaimAccess(ClaimAccessActions.DuplicateMatches, ClaimIdRouteValue = "id")]
    [ProducesResponseType(typeof(IReadOnlyList<ClaimDuplicateMatch>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ClaimDuplicateMatch>>> GetDuplicateMatches(string id)
    {
        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        var matchedClaims = new Dictionary<string, Claim>(StringComparer.Ordinal);
        foreach (var matchedId in DuplicateMatchBuilder.MatchedClaimIds(claim))
        {
            var matched = await _claimRepository.GetByIdAsync(matchedId);
            if (matched is not null)
            {
                matchedClaims[matchedId] = matched;
            }
        }

        return Ok(DuplicateMatchBuilder.Build(claim, matchedClaims));
    }

    /// <summary>
    /// Get claim by claim number
    /// </summary>
    [HttpGet("number/{claimNumber}")]
    [AuditClaimAccess(ClaimAccessActions.View, ClaimNumberRouteValue = "claimNumber")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Claim>> GetClaimByNumber(string claimNumber)
    {
        _logger.LogInformation("Fetching claim by number: {ClaimNumber}", LogSanitizer.Sanitize(claimNumber));

        var claim = await _claimRepository.GetByClaimNumberAsync(claimNumber);
        if (claim == null)
        {
            return NotFound($"Claim {claimNumber} not found");
        }

        await _diagnosisMetadataEnricher.EnrichAsync(claim);
        return Ok(claim);
    }

    /// <summary>
    /// Claims whose 837 PWK named this attachment control number (PWK06): how
    /// a 275 (TRN02) finds its claim. Tenant-scoped like every claim read;
    /// answers only identifiers and the billing provider, so the caller can
    /// tell two providers' claims apart. The id is the claim's stable version
    /// chain id (its first version's id), so an attachment stays with the
    /// claim through later adjustments. An empty list when nothing matches.
    /// </summary>
    [HttpGet("attachment-control-numbers/{controlNumber}")]
    [ProducesResponseType(typeof(IReadOnlyList<AttachmentControlNumberMatch>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<AttachmentControlNumberMatch>>> FindByAttachmentControlNumber(
        string controlNumber, CancellationToken ct)
    {
        var trimmed = controlNumber?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > 80 || trimmed.Any(char.IsControl))
        {
            return BadRequest("An attachment control number is 1-80 characters.");
        }

        var claims = await _claimRepository.FindByAttachmentControlNumberAsync(trimmed, limit: 20, ct);
        return Ok(claims.Select(c => new AttachmentControlNumberMatch
        {
            ClaimId = string.IsNullOrWhiteSpace(c.ClaimVersionId) ? c.Id : c.ClaimVersionId,
            ClaimNumber = c.ClaimNumber,
            BillingProviderNPI = c.BillingProviderNPI,
            SubmittedDate = c.SubmittedDate,
            Reports = c.AttachmentReports.Where(r => r.ControlNumber == trimmed).ToList(),
        }).ToList());
    }

    /// <summary>
    /// Search claims (by member, provider, date range, status)
    /// </summary>
    [HttpGet("search")]
    [AuditClaimAccess(ClaimAccessActions.Search)]
    [ProducesResponseType(typeof(IEnumerable<Claim>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Claim>>> SearchClaims(
        [FromQuery] string? memberId = null,
        [FromQuery] string? providerNPI = null,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] ClaimStatus? status = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation(
            "Searching claims: member={Member}, provider={Provider}, dateFrom={From}, dateTo={To}, status={Status}, lob={LOB}",
            LogSanitizer.Sanitize(memberId), LogSanitizer.Sanitize(providerNPI), serviceDateFrom, serviceDateTo, status, lineOfBusiness);

        var claims = await _claimRepository.SearchAsync(
            memberId, providerNPI, serviceDateFrom, serviceDateTo, status, lineOfBusiness, page, pageSize);

        await _diagnosisMetadataEnricher.EnrichAsync(claims);
        return Ok(claims);
    }

    /// <summary>Search claims via POST body (portal search form).</summary>
    [HttpPost("search")]
    [AuditClaimAccess(ClaimAccessActions.Search)]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchClaimsPost([FromBody] ClaimSearchBody body, CancellationToken ct = default)
    {
        ClaimStatus? status = null;
        if (!string.IsNullOrEmpty(body.Status) && Enum.TryParse<ClaimStatus>(body.Status, true, out var parsed))
            status = parsed;

        if (!string.IsNullOrWhiteSpace(body.RunId))
        {
            var tenantId = GetTenantId();
            var run = await _massAdjudicationRunRepository.GetAsync(tenantId, body.RunId, ct);
            if (run is null)
            {
                return Ok(new { claims = Array.Empty<Claim>(), totalCount = 0, pageNumber = body.PageNumber, pageSize = body.PageSize });
            }

            var submittedClaimIds = await _massAdjudicationRunRepository.ListSubmittedClaimIdsAsync(tenantId, body.RunId, ct);
            var (runPage, runTotalCount) = await _claimRepository.SearchByIdsAsync(
                submittedClaimIds,
                body.MemberId,
                body.ProviderId,
                body.ServiceDateFrom,
                body.ServiceDateTo,
                status,
                body.PageNumber,
                body.PageSize);

            await _diagnosisMetadataEnricher.EnrichAsync(runPage, ct);
            return Ok(new { claims = runPage, totalCount = runTotalCount, pageNumber = body.PageNumber, pageSize = body.PageSize });
        }

        // Claim number, authorization number and the Claims Insights
        // drill-down filters go through ClaimCriteria, the same definition
        // the Insights figures are counted with. (Before it existed the
        // claim and auth number were silently ignored.)
        if (body.UsesCriteria)
        {
            var criteria = new ClaimCriteria
            {
                ClaimNumber = body.ClaimNumber,
                AuthorizationNumber = body.AuthorizationNumber,
                MemberId = body.MemberId,
                ProviderNpi = body.ProviderId,
                ServiceDateFrom = body.ServiceDateFrom,
                ServiceDateTo = body.ServiceDateTo,
                Status = status,
                Open = body.Open == true,
                Adjudicated = body.Adjudicated == true,
                ReceivedAfter = body.ReceivedAfter,
                ReceivedOnOrBefore = body.ReceivedOnOrBefore,
                AdjudicatedFrom = body.AdjudicatedFrom,
                AdjudicatedBefore = body.AdjudicatedBefore,
                PendCode = body.PendCode,
                DenialReasonCode = body.DenialReasonCode,
                AutoAdjudicated = body.AutoAdjudicated,
                LineOfBusiness = body.LineOfBusiness,
            };
            var (matched, matchedCount) = await _claimRepository.SearchByCriteriaAsync(criteria, body.PageNumber, body.PageSize);

            await _diagnosisMetadataEnricher.EnrichAsync(matched, ct);
            return Ok(new { claims = matched, totalCount = matchedCount, pageNumber = body.PageNumber, pageSize = body.PageSize });
        }

        var (claims, totalCount) = await _claimRepository.SearchWithCountAsync(
            body.MemberId,
            body.ProviderId,
            body.ServiceDateFrom,
            body.ServiceDateTo,
            status,
            null,
            body.PageNumber,
            body.PageSize);

        await _diagnosisMetadataEnricher.EnrichAsync(claims, ct);
        return Ok(new { claims, totalCount, pageNumber = body.PageNumber, pageSize = body.PageSize });
    }

    /// <summary>
    /// Update a claim's operational (non-outcome) status — 277-style intake
    /// transitions (<see cref="ClaimStatus.Received"/>,
    /// <see cref="ClaimStatus.InAdjudication"/>) and, with
    /// <c>claims:void</c>, <see cref="ClaimStatus.Voided"/>.
    ///
    /// <para>
    /// This endpoint never sets an adjudicated outcome. The adjudication
    /// pipeline (consumer → <c>ClaimAdjudicationOrchestrator</c> →
    /// <c>PersistenceStage</c>) is the only writer of Approved / Denied /
    /// Pended, and Paid / PartiallyPaid come from remittance. Requests for
    /// those statuses are rejected with 422 and a pointer to the right path
    /// (<see cref="RejectOutcomeStatus"/>). The status write still goes
    /// through the shared precedence guard
    /// (<see cref="ClaimRepository.BlocksSynchronousWriteback"/>, via
    /// <see cref="IClaimRepository.TryTransitionStatusAsync"/>), so it can't
    /// move a Pended or finally-adjudicated claim back to an intake status;
    /// the response's <c>Status</c> reports what actually persisted.
    /// </para>
    /// </summary>
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<Claim>> UpdateClaimStatus(
        string id,
        [FromBody] ClaimStatusUpdate statusUpdate,
        CancellationToken ct = default)
    {
        if (ClaimStatusTransitionService.OutcomeStatusRefusal(statusUpdate.Status) is { } refusal)
        {
            return UnprocessableEntity(refusal);
        }

        // Voiding is claims:void everywhere; the generic status endpoint must
        // not be a way around the dedicated void actions.
        if (statusUpdate.Status == ClaimStatus.Voided && !_actor.HasPermission(ClaimsPermissions.Void))
        {
            return Forbid();
        }

        var result = await _statusTransitionService.TransitionAsync(
            id, statusUpdate, GetTenantId, TryGetCorrelationId(), ct);

        return result switch
        {
            ClaimStatusTransitionResult.Voided v =>
                v.VoidResult.Outcome is ClaimVoidOutcome.Voided or ClaimVoidOutcome.AlreadyVoided
                    ? Ok(v.VoidResult.Claim)
                    : VoidFailure(id, v.VoidResult),
            ClaimStatusTransitionResult.ClaimNotFound n => NotFound(n.Message),
            ClaimStatusTransitionResult.Updated u => Ok(u.Claim),
            _ => throw new UnreachableException($"Unhandled status transition result {result.GetType().Name}"),
        };
    }

    /// <summary>
    /// Write the AI Claims Examiner's advisory recommendation onto a pended claim.
    ///
    /// Called by claims-examiner-service after it processes a ClaimPendedEvent.
    /// Strictly advisory: this never changes Status, never touches AdjudicationResult,
    /// never touches PendDetails. It only writes Claim.AiExamination so the work queue
    /// can show the recommendation alongside the claim. A human examiner remains in
    /// the loop and must call /work-queue/{id}/override or /work-queue/{id}/assign
    /// to actually act on the claim.
    ///
    /// Idempotent on retry: a second call from the examiner service for the same
    /// claim simply replaces the prior recommendation. The ExaminerAgreement field
    /// is preserved across overwrites so we don't lose feedback signal if the model
    /// re-examines a claim a human has already acted on.
    /// </summary>
    [HttpPut("{id}/ai-examination")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Claim>> SetAiExamination(string id, [FromBody] AiExamination examination)
    {
        if (string.IsNullOrWhiteSpace(examination.RecommendedDisposition))
        {
            return BadRequest("recommendedDisposition is required");
        }

        var validDispositions = new[] { "Approve", "Deny", "RequestInfo", "EscalateToHuman" };
        if (!validDispositions.Contains(examination.RecommendedDisposition))
        {
            return BadRequest($"recommendedDisposition must be one of: {string.Join(", ", validDispositions)}");
        }

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        // Reject AI recommendations on claims that are no longer pended. The model
        // is only meaningful while the claim is awaiting human review; if the claim
        // has already been approved, denied, or paid, a stale recommendation could
        // confuse the work queue display.
        if (claim.Status != ClaimStatus.Pended)
        {
            _logger.LogWarning(
                "Rejected AI examination for claim {Id}: status is {Status}, not Pended",
                LogSanitizer.Sanitize(id), claim.Status);
            return Conflict($"Claim {id} is in status {claim.Status}, not Pended; AI examination ignored");
        }

        if (examination.GeneratedAt == default)
        {
            examination.GeneratedAt = DateTime.UtcNow;
        }

        // Preserve any prior examiner-agreement signal across re-examinations,
        // so we don't lose human feedback if the examiner service re-runs the model.
        if (claim.AiExamination?.ExaminerAgreement is { } prior)
        {
            examination.ExaminerAgreement = prior;
            examination.ExaminerActedAt = claim.AiExamination.ExaminerActedAt;
            examination.ExaminerUserId = claim.AiExamination.ExaminerUserId;
        }

        claim.AiExamination = examination;
        claim.LastUpdatedDate = DateTime.UtcNow;

        var updated = await _claimRepository.UpdateAsync(claim);

        // Append an immutable audit row for this recommendation. Snapshots
        // pend-detail context (rule id, code pair) so historical analyses can
        // correlate model accuracy with the specific deterministic edit type
        // even if the claim is later re-pended for a different reason.
        var firstEdit = claim.PendDetails?.EditFailures.FirstOrDefault();
        var audit = new AiExaminationAudit
        {
            TenantId = GetTenantId(),
            ClaimId = id,
            PendCode = claim.PendDetails?.PendCode,
            RuleId = firstEdit?.RuleId,
            Column1Code = firstEdit?.Column1Code,
            Column2Code = firstEdit?.Column2Code,
            RecommendedDisposition = examination.RecommendedDisposition,
            ConfidenceScore = examination.ConfidenceScore,
            Rationale = examination.Rationale,
            PolicyCitations = new List<string>(examination.PolicyCitations),
            ModelId = examination.ModelId,
            PromptVersion = examination.PromptVersion,
            GeneratedAt = examination.GeneratedAt
        };

        try
        {
            await _auditRepository.AppendAsync(audit);
        }
        catch (Exception ex)
        {
            // Audit-append failures must not break the live recommendation write.
            // The Claim.AiExamination update has already succeeded above; the
            // audit collection is the long-term record but the work-queue UI
            // can still function without the latest audit row.
            _logger.LogError(ex,
                "Failed to append AI examination audit for claim {Id}; live recommendation persisted, audit lost",
                LogSanitizer.Sanitize(id));
        }

        _logger.LogInformation(
            "AI examination set on claim {Id}: disposition={Disposition} confidence={Confidence:F2} model={Model} prompt={Prompt}",
            LogSanitizer.Sanitize(id),
            examination.RecommendedDisposition,
            examination.ConfidenceScore,
            LogSanitizer.Sanitize(examination.ModelId),
            LogSanitizer.Sanitize(examination.PromptVersion));

        return Ok(updated);
    }

    /// <summary>
    /// Get the full audit history of AI Claims Examiner recommendations for a claim,
    /// newest first. Each row is the immutable record of one model run; the live
    /// "current" recommendation is on Claim.AiExamination.
    ///
    /// This endpoint powers the work-queue UI's "show prior AI runs" expander and
    /// the 90-day override-rate analysis pipeline (joined on PromptVersion x
    /// RecommendedDisposition x ExaminerAgreement).
    /// </summary>
    [HttpGet("{id}/ai-examination/audit")]
    [AuditClaimAccess(ClaimAccessActions.AiExamination, ClaimIdRouteValue = "id")]
    [ProducesResponseType(typeof(IEnumerable<AiExaminationAudit>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AiExaminationAudit>>> GetAiExaminationAudit(
        string id, CancellationToken ct = default)
    {
        var history = await _auditRepository.GetByClaimAsync(id, TryGetTenantId(), ct);
        return Ok(history);
    }

    /// <summary>
    /// Record an examiner's agreement (or disagreement) with a prior AI recommendation.
    /// This is the feedback signal the 90-day override-rate analysis depends on.
    /// Call this from the work-queue UI whenever an examiner acts on a claim that
    /// has an AI recommendation attached.
    ///
    /// Values for `agreement`: Accepted | Modified | Overridden.
    /// </summary>
    [HttpPost("{id}/ai-examination/agreement")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Claim>> SetAiExaminerAgreement(
        string id,
        [FromBody] AiExaminerAgreementRequest request)
    {
        var validAgreements = new[] { "Accepted", "Modified", "Overridden" };
        if (string.IsNullOrWhiteSpace(request.Agreement) || !validAgreements.Contains(request.Agreement))
        {
            return BadRequest($"agreement must be one of: {string.Join(", ", validAgreements)}");
        }

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim == null) return NotFound($"Claim {id} not found");

        if (claim.AiExamination == null)
        {
            return BadRequest($"Claim {id} has no AI examination to mark agreement on");
        }

        // The examiner is the authenticated caller; a body-supplied
        // ExaminerUserId is ignored.
        var examinerUserId = ResolveActorId();
        claim.AiExamination.ExaminerAgreement = request.Agreement;
        claim.AiExamination.ExaminerActedAt = DateTime.UtcNow;
        claim.AiExamination.ExaminerUserId = examinerUserId;
        claim.LastUpdatedDate = DateTime.UtcNow;
        claim.LastUpdatedBy = examinerUserId;

        var updated = await _claimRepository.UpdateAsync(claim);

        // Cascade the agreement to the latest audit row. This is the only mutation
        // permitted on an audit record and is enforced single-write at the repository.
        // A null return means there was no audit row to update (recommendation may
        // have been written before the audit collection existed) — log and continue.
        var auditUpdated = await _auditRepository.SetExaminerAgreementAsync(
            id, GetTenantId(), request.Agreement, examinerUserId, request.Notes);

        if (auditUpdated is null)
        {
            _logger.LogWarning(
                "No audit row found for claim {Id} when setting examiner agreement; live AiExamination updated only",
                LogSanitizer.Sanitize(id));
        }

        _logger.LogInformation(
            "Examiner {User} marked claim {Id} as {Agreement} (recommended {Disposition})",
            LogSanitizer.Sanitize(examinerUserId), LogSanitizer.Sanitize(id),
            request.Agreement, claim.AiExamination.RecommendedDisposition);

        return Ok(updated);
    }

    /// <summary>
    /// Process remittance for a claim (835 transaction). Sent by
    /// payment-service during PaymentRun execution and by manual
    /// remittance-posting tools. 5.10 routes Paid transitions through
    /// <see cref="IClaimFinalizationService"/> so the version-event
    /// chain (<c>ClaimVersionPaid</c>) and Kafka notification
    /// (<c>claims.finalized.v1</c>) fire alongside the legacy Status
    /// update; idempotent when the same CheckNumber arrives twice;
    /// returns 409 on CheckNumber mismatch and 422 when the source claim
    /// is not in a Paid-eligible state.
    ///
    /// <para>
    /// A zero-payment remittance finalizes exactly like a paid one: a claim
    /// the pipeline Approved with a $0 plan payment (its allowed amount
    /// fully applied to deductible / copay / coinsurance) is payable, and is
    /// moved to Paid through the same service, so its accumulator commit
    /// (the deductible credit) is kept rather than dropped as a denial. When
    /// no check / EFT trace is supplied, the remittance control number stands
    /// in for it (an 835 with no payment still carries a TRN02 reference),
    /// so a repeat of the same remittance is an idempotent no-op. The
    /// payment cycle finalizes both adjudicated outcomes: Approved ("Pay") →
    /// Paid, and a $0 remittance on a Deny claim (what a denial's 835
    /// carries) → Denied. A $0 remittance on an already-Denied claim is an
    /// idempotent 200, unchanged; a paid remittance on a Deny / Denied claim
    /// and any other ineligible source state (Pended, Submitted, ...) answer
    /// 422, unchanged. A negative amount is rejected (400): a recoupment is
    /// a reversal run, not a remittance.
    /// </para>
    /// </summary>
    [HttpPost("{id}/remittance")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<Claim>> ProcessRemittance(
        string id,
        [FromBody] RemittanceUpdate remittance)
    {
        _logger.LogInformation(
            "Processing remittance for claim {Id}, check number {CheckNumber}",
            LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(remittance.CheckNumber));

        if (remittance.PaymentAmount < 0m)
        {
            return BadRequest(new { message = "PaymentAmount cannot be negative; a recoupment is a reversal run (POST {id}/void)" });
        }

        var zeroPay = remittance.PaymentAmount == 0m;

        // The check / EFT trace the finalize is keyed on (idempotency and
        // conflict detection). A $0 remittance may carry none: its control
        // number stands in, deterministically, so a repeat is a no-op.
        var checkNumber = !string.IsNullOrWhiteSpace(remittance.CheckNumber)
            ? remittance.CheckNumber
            : zeroPay && !string.IsNullOrWhiteSpace(remittance.ControlNumber)
                ? remittance.ControlNumber
                : null;
        if (checkNumber is null)
        {
            return BadRequest(new
            {
                message = zeroPay
                    ? "A zero-payment remittance needs a CheckNumber or a ControlNumber"
                    : "CheckNumber is required for a paid remittance",
            });
        }

        var request = new ClaimFinalizationRequest
        {
            CheckNumber = checkNumber,
            PaymentDate = remittance.PaymentDate,
            PayerPayment = remittance.PaymentAmount,
            PaymentRunId = remittance.PaymentRunId,
            EraEnvelopeId = remittance.EraEnvelopeId,
            EdiControlNumber = remittance.ControlNumber
        };

        var result = await _finalizationService.FinalizeAsync(
            id, request, GetTenantId(), TryGetActorId(), TryGetCorrelationId());

        switch (result.Outcome)
        {
            case ClaimFinalizationOutcome.Finalized:
            case ClaimFinalizationOutcome.AlreadyFinalized:
                // EDI835ControlNumber is persisted by ClaimFinalizationService
                // as part of the same non-terminal write — a follow-up
                // UpdateAsync would trip the repository's terminal-state guard.
                return Ok(result.Claim);

            case ClaimFinalizationOutcome.NotFound:
                return NotFound(new { message = result.Message ?? $"Claim {id} not found" });

            case ClaimFinalizationOutcome.Conflict:
                return Conflict(new { message = result.Message, currentStatus = result.Claim?.Status });

            case ClaimFinalizationOutcome.InvalidSourceState:
                return UnprocessableEntity(new { message = result.Message, currentStatus = result.Claim?.Status });

            default:
                return StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Record an inbound payer 835 on a domain claim. Distinct from
    /// <c>POST {id}/remittance</c> (PaymentRun finalize + outbound 835).
    /// Does not emit <c>claims.finalized.v1</c>. Missing claims return 404
    /// so gateway-only transmissions skip rather than fail posting.
    /// </summary>
    [HttpPost("{id}/inbound-remittance")]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> PostInboundRemittance(
        string id,
        [FromBody] InboundRemittancePostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RemittanceId))
        {
            return BadRequest(new { message = "RemittanceId is required" });
        }

        var claim = await _claimRepository.GetByIdAsync(id);
        if (claim is null)
        {
            return NotFound(new { message = $"Claim {id} not found" });
        }

        if (string.Equals(claim.InboundRemittanceId, request.RemittanceId, StringComparison.Ordinal))
        {
            return Conflict(new { message = "already-posted" });
        }

        if (!string.IsNullOrWhiteSpace(claim.InboundRemittanceId))
        {
            return UnprocessableEntity(new { message = "claim-not-postable" });
        }

        _logger.LogInformation(
            "Recording inbound remittance for claim {Id}",
            LogSanitizer.Sanitize(id));

        claim.InboundRemittanceId = request.RemittanceId;
        claim.AdjudicationResult ??= new AdjudicationResult();
        claim.AdjudicationResult.PayerPayment = request.PaymentAmount;
        claim.AdjudicationResult.PatientResponsibility = request.PatientResponsibility;
        if (request.PaymentDate != default)
        {
            claim.AdjudicationResult.PaymentDate = request.PaymentDate;
        }

        claim.LastUpdatedDate = DateTime.UtcNow;
        claim.LastUpdatedBy = TryGetActorId();

        try
        {
            var updated = await _claimRepository.UpdateAsync(claim);
            return Ok(updated);
        }
        catch (ClaimVersionStateException ex) when (ex.IsNotFound)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ClaimVersionStateException)
        {
            return UnprocessableEntity(new { message = "claim-not-postable" });
        }
    }

    /// <summary>
    /// Void a Paid/PartiallyPaid/Adjusted claim (5.12b). Routes to
    /// <see cref="IClaimFinalizationService.VoidAsync"/>; idempotent on
    /// re-invocation (returns <see cref="ClaimVoidOutcome.AlreadyVoided"/>
    /// → 200 OK). Sent by <c>payment-service</c> ReversalRunService during
    /// reversal-run execution; the optional
    /// <c>ReversalRunId</c> body field correlates the void to the
    /// originating ReversalRun and triggers the adjustment-lifecycle
    /// transition (PendingReversal → Active) when the predecessor was
    /// part of an in-flight adjustment chain.
    ///
    /// <para>Pattern parity with <c>POST /api/claims/{id}/remittance</c>
    /// (the Adjudicated → Paid surface from 5.10).</para>
    /// </summary>
    [HttpPost("{id}/void")]
    [RequirePermission(ClaimsPermissions.Void)]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<Claim>> VoidClaim(
        string id,
        [FromBody] ClaimVoidRequest? request,
        CancellationToken ct = default)
    {
        if (request == null)
        {
            return BadRequest(new { message = "Request body is required" });
        }
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { message = "Reason is required for the audit trail" });
        }

        var result = await _voidService.VoidClaimAsync(id, request, GetTenantId, TryGetCorrelationId(), ct);

        return result.Outcome switch
        {
            ClaimVoidOutcome.Voided => Ok(result.Claim),
            ClaimVoidOutcome.AlreadyVoided => Ok(result.Claim),
            ClaimVoidOutcome.NotFound => NotFound(new { message = result.Message ?? $"Claim {id} not found" }),
            ClaimVoidOutcome.InvalidSourceState => UnprocessableEntity(new
            {
                message = result.Message,
                currentStatus = result.Claim?.Status.ToString(),
                currentVersionState = result.Claim?.VersionState.ToString(),
            }),
            _ => StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    // Identity.Name is the display name claim, and X-User-Id is caller-supplied;
    // the actor is the token subject.
    private string? TryGetActorId() => ResolveActorId();

    private string? TryGetCorrelationId() =>
        HttpContext?.Request?.Headers["X-Correlation-Id"].FirstOrDefault()
        ?? Activity.Current?.Id;

    /// <summary>
    /// Which of these prior authorization numbers claims carry, and on how
    /// many current claims: authorization-service's "approved but unused"
    /// figure. Counts only, no claim detail; at most 2,000 numbers.
    /// </summary>
    [HttpPost("authorization-usage")]
    [ProducesResponseType(typeof(IReadOnlyList<AuthorizationUsage>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<AuthorizationUsage>>> GetAuthorizationUsage(
        [FromBody] AuthorizationUsageRequest request, CancellationToken ct = default)
    {
        if (request.AuthorizationNumbers.Count > AuthorizationUsageRequest.MaxNumbers)
            return BadRequest($"At most {AuthorizationUsageRequest.MaxNumbers} authorization numbers per request.");

        var usage = await _claimRepository.GetAuthorizationUsageAsync(request.AuthorizationNumbers, ct);
        return Ok(usage.OrderBy(u => u.Key, StringComparer.Ordinal)
            .Select(u => new AuthorizationUsage(u.Key, u.Value)).ToList());
    }

    /// <summary>
    /// Claims Insights (portal-navigation.md §5.1): open inventory and aging
    /// now, pend reasons now, and outcomes, auto-adjudication rate, denial
    /// reasons and receipt-to-adjudication turnaround for claims adjudicated
    /// in [from, to). Aggregates only: no member or claim detail. Each figure
    /// counts <see cref="ClaimCriteria"/> matches, which the claims search
    /// accepts as its drill-down.
    /// </summary>
    [HttpGet("insights")]
    [ProducesResponseType(typeof(ClaimInsights), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClaimInsights>> GetClaimInsights(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var toDate = (to ?? now).ToUniversalTime();
        var fromDate = (from ?? toDate.AddDays(-30)).ToUniversalTime();
        if (fromDate >= toDate)
            return BadRequest("from must be before to.");
        if (toDate - fromDate > TimeSpan.FromDays(366))
            return BadRequest("The period can be at most a year.");

        return Ok(await _claimRepository.GetInsightsAsync(fromDate, toDate, lineOfBusiness, now, ct));
    }

    /// <summary>
    /// Get claims summary statistics
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(ClaimsSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<ClaimsSummary>> GetClaimsSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Fetching claims summary from {From} to {To}, lob={LOB}",
            fromDate, toDate, lineOfBusiness);

        var summary = await _claimRepository.GetClaimsSummaryAsync(fromDate, toDate, lineOfBusiness);
        return Ok(summary);
    }

    /// <summary>
    /// Download the X12 277CA Claim Acknowledgment for a claim
    /// </summary>
    [HttpGet("{id}/277ca")]
    [AuditClaimAccess(ClaimAccessActions.Acknowledgment, ClaimIdRouteValue = "id")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClaimAcknowledgment(
        string id,
        [FromServices] CloudHealthOffice.Infrastructure.Edi.Interchange.IOutboundInterchangeTracker? outboundTracker = null)
    {
        var claim = await _claimRepository.GetByIdAsync(id);

        if (claim == null)
        {
            return NotFound($"Claim {id} not found");
        }

        var cfg = new ClaimAcknowledgmentConfig
        {
            InterchangeSenderId   = _configuration["Ack:InterchangeSenderId"]   ?? "CHO",
            InterchangeReceiverId = _configuration["Ack:InterchangeReceiverId"] ?? "RECEIVER",
            ApplicationSenderId   = _configuration["Ack:ApplicationSenderId"]   ?? "CHO",
            ApplicationReceiverId = _configuration["Ack:ApplicationReceiverId"] ?? "RECEIVER",
            PayerName             = _configuration["Ack:PayerName"]             ?? "Cloud Health Office",
            PayerId               = _configuration["Ack:PayerId"]               ?? "CHO",
            PayerOriginatorId     = _configuration["Ack:PayerOriginatorId"]     ?? "CHO",
        };

        _logger.LogInformation(
            "Generating 277CA for claim {ClaimId} ({ClaimNumber}), status={Status}",
            LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(claim.ClaimNumber), claim.Status);

        var edi = _ackService.Generate277CA(claim, cfg);

        // Remember the 277CA's envelope so the submitter's TA1 for it can be
        // matched. One interchange per claim status: a repeat download reuses
        // the ISA13 already recorded instead of minting a new one.
        if (outboundTracker is not null)
            edi = await outboundTracker.GetOrRecordSentAsync(TryGetTenantId(), edi, "277CA", $"{claim.Id}:{claim.Status}",
                HttpContext?.RequestAborted ?? CancellationToken.None);

        var filename = $"277CA_{claim.ClaimNumber}.edi";
        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        return Content(edi, "text/plain");
    }

    /// <summary>
    /// Delete claim (soft delete: void it). Routed through
    /// <see cref="IClaimFinalizationService.VoidAsync"/>, the same transition
    /// as <c>POST {id}/void</c>: the void saves the claim's terminal
    /// accumulator reversal with the void write and emits
    /// <c>ClaimVersionVoided</c>. Only Paid / PartiallyPaid / Adjusted claims
    /// can be voided (422 otherwise, unchanged); a repeat is a no-op (204).
    /// </summary>
    [HttpDelete("{id}")]
    [RequirePermission(ClaimsPermissions.Void)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> VoidClaim(string id, CancellationToken ct = default)
    {
        var result = await _voidService.DeleteClaimAsync(id, GetTenantId, TryGetCorrelationId(), ct);
        return result.Outcome is ClaimVoidOutcome.Voided or ClaimVoidOutcome.AlreadyVoided
            ? NoContent()
            : VoidFailure(id, result);
    }

    private ActionResult VoidFailure(string id, ClaimVoidResult result) => result.Outcome switch
    {
        ClaimVoidOutcome.NotFound => NotFound(new { message = result.Message ?? $"Claim {id} not found" }),
        ClaimVoidOutcome.InvalidSourceState => UnprocessableEntity(new
        {
            message = result.Message,
            currentStatus = result.Claim?.Status.ToString(),
            currentVersionState = result.Claim?.VersionState.ToString(),
        }),
        _ => StatusCode(StatusCodes.Status500InternalServerError),
    };

    /// <summary>
    /// Get aggregated accumulator totals for a member or family for a plan year.
    ///
    /// Called by the Redis accumulator service on a cache miss to rebuild from claim
    /// history. Returns the sum of deductible, OOP, coinsurance, and copay amounts
    /// across all finalized claims for the given owner / plan / year combination.
    ///
    /// <list type="bullet">
    ///   <item><paramref name="ownerId"/> — memberId for Individual scope; subscriberId for Family scope.</item>
    ///   <item><paramref name="scope"/> — "Individual" or "Family".</item>
    ///   <item><paramref name="planYear"/> — four-digit year string, e.g. "2026".</item>
    /// </list>
    /// </summary>
    [HttpGet("accumulator-totals")]
    [ProducesResponseType(typeof(AccumulatorTotalsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AccumulatorTotalsResponse>> GetAccumulatorTotals(
        [FromQuery] string ownerId,
        [FromQuery] string scope,
        [FromQuery] string benefitPlanId,
        [FromQuery] string planYear,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return BadRequest("ownerId is required");
        if (scope != "Individual" && scope != "Family")
            return BadRequest("scope must be 'Individual' or 'Family'");
        if (string.IsNullOrWhiteSpace(benefitPlanId))
            return BadRequest("benefitPlanId is required");
        if (!int.TryParse(planYear, out _))
            return BadRequest("planYear must be a four-digit year, e.g. '2026'");

        _logger.LogDebug(
            "Accumulator totals request: owner={OwnerId}, scope={Scope}, plan={PlanId}, year={Year}",
            LogSanitizer.Sanitize(ownerId), scope, LogSanitizer.Sanitize(benefitPlanId), planYear);

        var result = await _claimRepository.GetAccumulatorTotalsAsync(
            ownerId, scope, benefitPlanId, planYear, ct);

        return Ok(result);
    }

    // ── Work Queue endpoints ────────────────────────────────────────────
    // These power the portal's Claims Work Queue page. Work queue items
    // are derived from claims in Pended status.

    /// <summary>
    /// Get work queue summary counts by pend reason
    /// </summary>
    [HttpGet("work-queue/summary")]
    [ProducesResponseType(typeof(WorkQueueSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<WorkQueueSummary>> GetWorkQueueSummary()
    {
        var pendedClaims = (await _claimRepository.SearchAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: ClaimStatus.Pended, lineOfBusiness: null,
            page: 1, pageSize: 1000)).ToList();

        // Prefer PendDetails (structured pend reason from workflow); fall back to
        // legacy AdjudicationResult.DenialReasonCode for pre-PendDetails claims.
        static string? PendCode(Claim c) =>
            c.PendDetails?.PendCode ?? c.AdjudicationResult?.DenialReasonCode;

        var summary = new WorkQueueSummary
        {
            NcciEditFailures = pendedClaims.Count(c => PendCode(c) is "NCCI" or "MUE"),
            MissingAuth = pendedClaims.Count(c => PendCode(c) is "AUTH" or "NOAUTH" or Services.Adjudication.Stages.BenefitCalculationStage.PriorAuthorizationUnavailablePendCode),
            ProviderNotContracted = pendedClaims.Count(c => PendCode(c) is "OON" or "NOCONTRACT"),
            CobRequired = pendedClaims.Count(c => PendCode(c) is "COB"),
            MedicalReview = pendedClaims.Count(c => PendCode(c) is "MEDREVIEW" or "CLINICAL")
        };

        // Claims without a recognized pend reason go to medical review as default
        var categorized = summary.NcciEditFailures + summary.MissingAuth +
                          summary.ProviderNotContracted + summary.CobRequired + summary.MedicalReview;
        summary.MedicalReview += pendedClaims.Count - categorized;

        return Ok(summary);
    }

    /// <summary>
    /// Get work queue items (pended claims for examiner review)
    /// </summary>
    [HttpGet("work-queue/items")]
    [AuditClaimAccess(ClaimAccessActions.WorkQueue)]
    [ProducesResponseType(typeof(IEnumerable<WorkQueueItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<WorkQueueItem>>> GetWorkQueueItems(
        [FromQuery] string? queueType = null,
        [FromQuery] string? assignedTo = null,
        [FromQuery] int limit = 100)
    {
        var pendedClaims = (await _claimRepository.SearchAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: ClaimStatus.Pended, lineOfBusiness: null,
            page: 1, pageSize: limit)).ToList();

        var items = pendedClaims.Select(c =>
        {
            // Prefer the structured PendDetails the adjudication pipeline persisted;
            // fall back to legacy AdjudicationResult.DenialReasonCode for claims that
            // pre-date the PendDetails field.
            var pendCode = c.PendDetails?.PendCode ?? c.AdjudicationResult?.DenialReasonCode;
            return new WorkQueueItem
            {
                ClaimId = c.Id,
                ClaimNumber = c.ClaimNumber ?? string.Empty,
                MemberName = c.SubscriberLastName != null ? $"{c.SubscriberFirstName} {c.SubscriberLastName}" : c.MemberId,
                MemberId = c.MemberId,
                ProviderName = c.BillingProviderName ?? c.BillingProviderNPI,
                ServiceDate = c.ClaimLines.FirstOrDefault()?.ServiceDateFrom ?? c.CreatedDate,
                QueueReason = MapPendReason(pendCode),
                QueueReasonCode = pendCode ?? "REVIEW",
                PendReasons = ClaimResolutionService.PendReasonsOf(c),
                PendFingerprint = PendDetails.ComputeFingerprint(c.PendDetails),
                DaysInQueue = (int)(DateTime.UtcNow - c.LastUpdatedDate).TotalDays,
                Priority = (DateTime.UtcNow - c.LastUpdatedDate).TotalDays > 14 ? "High" :
                           (DateTime.UtcNow - c.LastUpdatedDate).TotalDays > 7 ? "Medium" : "Low",
                AssignedTo = "",
                TotalCharged = c.TotalChargeAmount,
                ProcedureCodes = c.ClaimLines.Select(sl => sl.ProcedureCode).ToList(),
                AiRecommendedDisposition = c.AiExamination?.RecommendedDisposition,
                AiConfidenceScore = c.AiExamination?.ConfidenceScore,
                AiRationale = c.AiExamination?.Rationale,
                AiPolicyCitations = c.AiExamination?.PolicyCitations ?? new List<string>(),
                AiExaminerAgreement = c.AiExamination?.ExaminerAgreement,
                DuplicateMatchedClaimNumbers = (c.PendDetails?.DuplicateFindings ?? new List<DuplicateFindingSnapshot>())
                    .Select(f => string.IsNullOrWhiteSpace(f.MatchedClaimNumber) ? f.MatchedClaimId : f.MatchedClaimNumber)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            };
        }).ToList();

        if (!string.IsNullOrEmpty(queueType))
            items = items.Where(i => i.QueueReasonCode == queueType).ToList();

        return Ok(items);
    }

    /// <summary>
    /// Work-management reconcile source: every Pended claim of the caller's
    /// tenant as a <see cref="CloudHealthOffice.Events.ClaimWorkEvent"/>
    /// work-needed snapshot, built by the same mapping as the
    /// <c>claims.work.v1</c> events (<see cref="ClaimWorkEvents"/>), one page
    /// at a time. work-management-service pages through it to create missing
    /// work items and close items whose claim is no longer pended. Every
    /// snapshot carries the same <c>sourceVersion</c>: the moment this read
    /// started, so a transition published after it wins over the snapshot.
    /// </summary>
    [HttpGet("work-queue/snapshots")]
    [ProducesResponseType(typeof(WorkSnapshotPage), StatusCodes.Status200OK)]
    public async Task<ActionResult<WorkSnapshotPage>> GetWorkSnapshots(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 500)
    {
        var observedAt = DateTime.UtcNow;
        var tenantId = GetTenantId();
        var safePageSize = Math.Clamp(pageSize, 1, 1000);
        var safePage = Math.Max(page, 1);
        var (claims, total) = await _claimRepository.SearchWithCountAsync(
            memberId: null, providerNPI: null,
            serviceDateFrom: null, serviceDateTo: null,
            status: ClaimStatus.Pended, lineOfBusiness: null,
            page: safePage, pageSize: safePageSize);

        return Ok(new WorkSnapshotPage
        {
            Page = safePage,
            PageSize = safePageSize,
            TotalCount = total,
            ObservedAt = observedAt,
            Items = claims.Select(c => ClaimWorkEvents.Needed(c, tenantId, null, observedAt)).ToList(),
        });
    }

    /// <summary>
    /// Accumulator adjustment reviews: claims whose engine accumulator commit
    /// was clamped at a limit (a concurrent claim for the member or family
    /// reached the deductible / OOP maximum first), or refused by the store
    /// although the claim is finalized (its id is fenced). The paid amounts
    /// were not changed; an examiner decides whether an adjustment is due.
    /// </summary>
    [HttpGet("work-queue/accumulator-adjustments")]
    [ProducesResponseType(typeof(IEnumerable<AccumulatorAdjustmentItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AccumulatorAdjustmentItem>>> GetAccumulatorAdjustments([FromQuery] int limit = 100)
    {
        var claims = await _claimRepository.GetAccumulatorClampReviewsAsync(Math.Clamp(limit, 1, 1000), HttpContext.RequestAborted);
        return Ok(claims.Select(c => new AccumulatorAdjustmentItem
        {
            ClaimId = c.Id,
            ClaimNumber = c.ClaimNumber,
            MemberId = c.MemberId,
            Status = c.Status.ToString(),
            RaisedAt = c.AccumulatorClampReview!.RaisedAt,
            Kind = c.AccumulatorClampReview.Kind ?? AccumulatorClampReview.Clamped,
            CommitId = c.AccumulatorClampReview.CommitId,
            Clamps = c.AccumulatorClampReview.Clamps,
        }).ToList());
    }

    /// <summary>Marks a claim's accumulator adjustment review resolved.</summary>
    [HttpPost("work-queue/{claimId}/accumulator-adjustment/resolve")]
    [RequirePermission(ClaimsPermissions.WorkQueueWork)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> ResolveAccumulatorAdjustment(string claimId) =>
        await _claimRepository.ResolveAccumulatorClampReviewAsync(claimId, ResolveActorId(), HttpContext.RequestAborted)
            ? NoContent()
            : NotFound();

    /// <summary>
    /// Assign a pended claim to an examiner
    /// </summary>
    [HttpPost("work-queue/{claimId}/assign")]
    [RequirePermission(ClaimsPermissions.WorkQueueAssign)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AssignClaim(string claimId, [FromBody] AssignClaimRequest request)
    {
        var claim = await _claimRepository.GetByIdAsync(claimId);
        if (claim == null) return NotFound();

        // In a full implementation, this would update an AssignedTo field.
        // For now, just log and return success.
        _logger.LogInformation("Claim {ClaimId} assigned to {AssignedTo}", LogSanitizer.Sanitize(claimId), LogSanitizer.Sanitize(request.AssignTo));
        return Ok(new { claimId, assignedTo = request.AssignTo });
    }

    /// <summary>
    /// Override a pended claim (supervisor action). This is a single-step
    /// approval: there is no separate override request record, so the
    /// approver is simply the authenticated supervisor.
    /// </summary>
    [HttpPost("work-queue/{claimId}/override")]
    [RequirePermission(ClaimsPermissions.OverrideApprove)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> OverrideClaim(string claimId, [FromBody] OverrideClaimRequest request)
    {
        var result = await ResolvePendedClaim(
            claimId,
            new ResolvePendedClaimRequest
            {
                Disposition = "Approved",
                Reason = request.OverrideReason,
                PayerSequence = request.PayerSequence,
                PendFingerprint = request.PendFingerprint,
            });

        return result;
    }

    /// <summary>
    /// Resolve a pended claim through an explicit human-examiner action.
    /// Unlike the generic status endpoint, this path is allowed to cross the
    /// Pended review gate. It persists the final version state, records any
    /// AI-advisory feedback in the same claim write, and emits finalization.
    /// <para>
    /// PR #1278 round 3 and its verification:
    /// <list type="bullet">
    ///   <item><description>Every input is validated before anything
    ///     happens (re-run, reversal, write).</description></item>
    ///   <item><description>An approval must carry <c>pendFingerprint</c>,
    ///     the fingerprint of the pends the examiner viewed
    ///     (<see cref="PendDetails.Fingerprint"/>); if the stored pends
    ///     changed since, 409.</description></item>
    ///   <item><description>Approval re-adjudicates the claim (accumulators committed after the final write),
    ///     overriding only the stored pends (every one of them — a claim
    ///     that pended twice keeps both) with their exact reasons; a new pend
    ///     or a transient failure refuses it (409).</description></item>
    ///   <item><description><c>payerSequence</c> is accepted only when COB is
    ///     among the stored pends and only within 1..N (400 otherwise). A
    ///     value that disagrees with SBR01 needs <c>claims:override-approve</c>
    ///     (403) and a reason (400); one that disagrees with coverage-service
    ///     is refused by the re-run (409) without them. Sequence 1 over a
    ///     prior payment needs a second, different approver: the first call
    ///     is recorded (with the reviewed fingerprint and an expiry) and
    ///     answered 202; the second approver's call completes it.</description></item>
    ///   <item><description>Every resolution is recorded on the claim
    ///     (<see cref="Claim.ExaminerResolutions"/>, both approvers' reasons)
    ///     and on the ClaimVersionResolved event.</description></item>
    ///   <item><description>A denial reverses the engine accumulators the
    ///     claim wrote.</description></item>
    ///   <item><description>A resolution holds a conditional lock on the
    ///     Pended claim, and its final write is fenced on the lock token.</description></item>
    /// </list></para>
    /// </summary>
    [HttpPost("work-queue/{claimId}/resolve")]
    [RequirePermission(ClaimsPermissions.WorkQueueWork)]
    [ProducesResponseType(typeof(Claim), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> ResolvePendedClaim(
        string claimId,
        [FromBody] ResolvePendedClaimRequest request)
    {
        var result = await _resolutionService.ResolveAsync(
            claimId, request, GetTenantId, HttpContext?.TraceIdentifier ?? string.Empty,
            HttpContext?.RequestAborted ?? CancellationToken.None);

        return result switch
        {
            ClaimResolutionResult.Resolved r => Ok(r.Claim),
            ClaimResolutionResult.AwaitingSecondApproval a => Accepted(a.Body),
            ClaimResolutionResult.Invalid b => BadRequest(b.Message),
            ClaimResolutionResult.ClaimNotFound => NotFound(),
            ClaimResolutionResult.Conflict c => Conflict(c.Body),
            ClaimResolutionResult.Unprocessable u => UnprocessableEntity(u.Body),
            ClaimResolutionResult.Problem pd => Problem(statusCode: pd.StatusCode, title: pd.Title, detail: pd.Detail),
            _ => throw new UnreachableException($"Unhandled resolution result {result.GetType().Name}"),
        };
    }

    private static string MapPendReason(string? code) => code switch
    {
        "NCCI" or "MUE" => "NCCI Edit Failure",
        "AUTH" or "NOAUTH" => "Missing Authorization",
        Services.Adjudication.Stages.BenefitCalculationStage.PriorAuthorizationUnavailablePendCode => "Prior Authorization Lookup Unavailable",
        Services.Adjudication.Stages.BenefitCalculationStage.PlanUnavailablePendCode => "Benefit Plan Unavailable",
        Services.Adjudication.ClaimAdjudicationOrchestrator.ServiceUnavailablePendCode => "Service Unavailable",
        "OON" or "NOCONTRACT" => "Provider Not Contracted",
        "COB" => "COB Required",
        "MEDREVIEW" or "CLINICAL" => "Medical Review",
        "DUPLICATE" => "Possible Duplicate",
        Services.Adjudication.Stages.TimelyFilingStage.PendCode => "Timely Filing",
        Services.Adjudication.Stages.PlanEditsStage.PlanEditPendCode => "Plan Edit Review",
        _ => "Pending Review"
    };

}

/// <summary>One accumulator adjustment review (<c>GET work-queue/accumulator-adjustments</c>).</summary>
public class AccumulatorAdjustmentItem
{
    public string ClaimId { get; set; } = string.Empty;
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime RaisedAt { get; set; }

    /// <summary>Clamped (the store trimmed updates at a limit) or Refused (the store refused the commit of a finalized claim).</summary>
    public string Kind { get; set; } = AccumulatorClampReview.Clamped;
    public string CommitId { get; set; } = string.Empty;
    public List<CloudHealthOffice.BenefitEngine.Models.AccumulatorClamp> Clamps { get; set; } = new();
}

/// <summary>One page of <c>GET work-queue/snapshots</c>.</summary>
public class WorkSnapshotPage
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }

    /// <summary>When the read started; every item's <c>sourceVersion</c> is its tick count.</summary>
    public DateTime ObservedAt { get; set; }
    public List<CloudHealthOffice.Events.ClaimWorkEvent> Items { get; set; } = new();
}

public class WorkQueueSummary
{
    public int NcciEditFailures { get; set; }
    public int MissingAuth { get; set; }
    public int ProviderNotContracted { get; set; }
    public int CobRequired { get; set; }
    public int MedicalReview { get; set; }
}

public class WorkQueueItem
{
    public string ClaimId { get; set; } = string.Empty;

    /// <summary>Human-readable claim number for display; <see cref="ClaimId"/> stays the route key.</summary>
    public string ClaimNumber { get; set; } = string.Empty;
    public string MemberName { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public DateTime ServiceDate { get; set; }
    public string QueueReason { get; set; } = string.Empty;
    public string QueueReasonCode { get; set; } = string.Empty;

    /// <summary>Every pend reason, the routing one (<see cref="QueueReasonCode"/>) first.</summary>
    public List<string> PendReasons { get; set; } = new();

    /// <summary>
    /// Fingerprint of the pends shown (<see cref="PendDetails.Fingerprint"/>):
    /// sent back with an approval so it applies to exactly what was reviewed.
    /// </summary>
    public string PendFingerprint { get; set; } = string.Empty;
    public int DaysInQueue { get; set; }
    public string Priority { get; set; } = "Low";
    public string AssignedTo { get; set; } = string.Empty;
    public decimal TotalCharged { get; set; }
    public List<string> ProcedureCodes { get; set; } = new();

    /// <summary>AI examiner's recommended disposition, null if no AI run yet.</summary>
    public string? AiRecommendedDisposition { get; set; }

    /// <summary>AI examiner's self-reported confidence (0–1).</summary>
    public double? AiConfidenceScore { get; set; }

    /// <summary>Plain-English rationale shown alongside the claim in the work queue UI.</summary>
    public string? AiRationale { get; set; }

    /// <summary>Policy/rule citations the model relied on.</summary>
    public List<string> AiPolicyCitations { get; set; } = new();

    /// <summary>If a human has acted on this claim: Accepted, Modified, or Overridden.</summary>
    public string? AiExaminerAgreement { get; set; }

    /// <summary>
    /// Claim numbers (or ids, when no number was recorded) of the prior
    /// claims a DUPLICATE-pended claim matched. Empty otherwise.
    /// </summary>
    public List<string> DuplicateMatchedClaimNumbers { get; set; } = new();
}

public class AssignClaimRequest
{
    public string AssignTo { get; set; } = string.Empty;
}

public class OverrideClaimRequest
{
    public string OverrideReason { get; set; } = string.Empty;

    /// <summary>See <see cref="ResolvePendedClaimRequest.PayerSequence"/>.</summary>
    public int? PayerSequence { get; set; }

    /// <summary>See <see cref="ResolvePendedClaimRequest.PendFingerprint"/>.</summary>
    public string? PendFingerprint { get; set; }
}

public class EditDecisionRequest
{
    public string EditKey { get; set; } = string.Empty;

    /// <summary>Uphold or Override.</summary>
    public string Decision { get; set; } = string.Empty;

    /// <summary>Required for an override (the policy may require it for every override).</summary>
    public string? Reason { get; set; }
}

public class ManualLineDenialRequest
{
    public int LineNumber { get; set; }

    /// <summary>Units denied; omit to deny the whole line.</summary>
    public decimal? Units { get; set; }

    public string GroupCode { get; set; } = "CO";
    public string Carc { get; set; } = string.Empty;
    public string? Rarc { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class ResolvePendedClaimRequest
{
    public string Disposition { get; set; } = string.Empty;

    /// <summary>
    /// Edit-level decisions (workbench step 2). When present (even empty), the
    /// approval is edit-level: every edit on the pend not listed here is
    /// upheld at Finish. When absent, the approval is whole-claim, as before.
    /// </summary>
    public List<EditDecisionRequest>? EditDecisions { get; set; }

    /// <summary>Lines the reviewer denies on their own judgment.</summary>
    public List<ManualLineDenialRequest>? LineDenials { get; set; }

    public string? Reason { get; set; }
    public string? AiExaminerAgreement { get; set; }

    /// <summary>Ignored: the examiner is the authenticated caller.</summary>
    public string? ExaminerUserId { get; set; }

    /// <summary>
    /// For approving a claim pended for coordination of benefits: the payer
    /// order the examiner confirmed — 1 = this plan is primary, 2 =
    /// secondary, 3+ = tertiary or later, at most the payers on the claim
    /// (the 837 must then carry the earlier payers' 2320/2430 data).
    /// Required for a COB pend; rejected (400) for any other claim. A value
    /// that disagrees with SBR01 or coverage-service needs
    /// claims:override-approve and a reason; 1 over a prior payment needs a
    /// second, different approver.
    /// </summary>
    public int? PayerSequence { get; set; }

    /// <summary>
    /// The fingerprint of the pends the examiner viewed
    /// (<c>pendDetails.fingerprint</c>, or the work-queue item's
    /// <c>pendFingerprint</c>). Required to approve; a mismatch with the
    /// stored pends (the claim was re-adjudicated since) is 409.
    /// </summary>
    public string? PendFingerprint { get; set; }
}

public class ClaimAuditTimelineEntry
{
    public DateTime Timestamp { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Notes { get; set; }
    public string? EventType { get; set; }
    public int? Version { get; set; }
    public string? CorrelationId { get; set; }
}

public class AiExaminerAgreementRequest
{
    /// <summary>Accepted | Modified | Overridden.</summary>
    public string Agreement { get; set; } = string.Empty;

    /// <summary>Ignored: the examiner is the authenticated caller.</summary>
    public string ExaminerUserId { get; set; } = string.Empty;

    /// <summary>Optional free-text note (e.g., why the examiner overrode the AI).</summary>
    public string? Notes { get; set; }
}
