using CloudHealthOffice.Infrastructure.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AuthorizationService.Backends;
using AuthorizationService.Middleware;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using AuthorizationService.Services.Rfai;
using AuthorizationService.Services.Cms0057;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.Infrastructure.Logging;

namespace AuthorizationService.Controllers;

/// <summary>
/// Prior authorizations. The tenant and the acting user come from the
/// validated CHO token only (<see cref="ICurrentActor"/>); request bodies never
/// name the tenant or the actor. Reads need <c>authorizations:read</c>, writes
/// <c>authorizations:write</c> (defaults set in Program.cs). Review decisions
/// (278 response, status transitions, seeding decided fixtures) need
/// <c>authorizations:decide</c>.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AuthorizationsController : ControllerBase
{
    private readonly IAuthorizationRepository _authorizationRepository;
    private readonly IAuthorizationBackendSelector _backends;
    private readonly IWebHostEnvironment _environment;
    private readonly IPendedAuthorizationRfaiCoordinator? _rfai;
    private readonly ILogger<AuthorizationsController> _logger;
    private readonly ICurrentActor _currentActor;

    /// <summary>Decision actions: approve / modify / deny / pend / status transitions.</summary>
    public const string DecidePermission = "authorizations:decide";

    /// <summary>The token subject. Every write records this as its actor.</summary>
    private string Actor => _currentActor.UserId;

    /// <param name="rfai">
    /// Raises the CDex additional-information request an A4 decision implies.
    /// Optional so a deployment without rfai-service still records decisions —
    /// the pended status and its reason are unaffected; only the provider-facing
    /// documentation request is absent.
    /// </param>
    public AuthorizationsController(
        IAuthorizationRepository authorizationRepository,
        IAuthorizationBackendSelector backends,
        IWebHostEnvironment environment,
        ILogger<AuthorizationsController> logger,
        ICurrentActor currentActor,
        IPendedAuthorizationRfaiCoordinator? rfai = null)
    {
        _currentActor = currentActor;
        _authorizationRepository = authorizationRepository;
        _backends = backends;
        _environment = environment;
        _rfai = rfai;
        _logger = logger;
    }

    /// <summary>
    /// Report the active authorization backend (operating mode + backend).
    /// Makes Replace (CHO-native) vs Augment (external core) explicit for demos
    /// and diligence. Anonymous callers get the status only (configured or
    /// not); the mode, backend key and description go to authenticated CHO
    /// callers, so the external core a deployment fronts is not advertised.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("backend-status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetBackendStatus()
    {
        // Mode and the configured backend key never throw; resolving the backend
        // can (misconfigured Augment). Report that as a structured "misconfigured"
        // status rather than an unstructured 500.
        var mode = _backends.Mode;
        var backendKey = _backends.ActiveBackendKey;
        bool? authoritative;
        bool configured;
        string description;

        try
        {
            authoritative = _backends.Resolve().IsAuthoritative;
            configured = true;
            description = mode == CloudHealthOffice.OperatingMode.EngineOperatingMode.Replace
                ? "Cloud Health Office owns the authoritative authorization record (Replace mode)."
                : $"Cloud Health Office fronts an external core ('{backendKey}') that remains authoritative (Augment mode).";
        }
        catch (InvalidOperationException)
        {
            // Do not leak the exception detail; state that the configured backend
            // is not available.
            authoritative = null;
            configured = false;
            description = $"Operating mode is '{mode}' but backend '{backendKey}' is not registered/available. " +
                          "Configure Cms0057:Authorization to a registered backend.";
        }

        if (User.Identity?.IsAuthenticated != true)
            return Ok(new { status = configured ? "ok" : "misconfigured" });

        return Ok(new
        {
            status = configured ? "ok" : "misconfigured",
            operatingMode = mode.ToString(),
            backend = backendKey,
            configured,
            authoritative,
            description,
        });
    }

    /// <summary>
    /// Submit new prior authorization request (278 request)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Authorization>> SubmitAuthorization([FromBody] Authorization authorization)
    {
        var receivedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "Submitting authorization for member {MemberId}, provider {ProviderNPI}, service date {ServiceDate}",
            LogSanitizer.MaskId(authorization.MemberId), LogSanitizer.Sanitize(authorization.RequestingProviderNPI), authorization.RequestedServiceDateFrom);

        // Validate authorization
        if (authorization.RequestedServices.Count == 0)
        {
            return BadRequest("Authorization must have at least one requested service");
        }

        authorization.Id = Guid.NewGuid().ToString();
        authorization.Status = AuthorizationStatus.Submitted;
        authorization.SubmittedDate = receivedAt;
        authorization.CreatedDate = DateTime.UtcNow;
        authorization.LastUpdatedDate = DateTime.UtcNow;

        // The body is the full persisted model, so everything a requester must
        // not set is overwritten here: the tenant and the actor come from the
        // token, and a new request carries no review outcome (that needs
        // authorizations:decide on the decision endpoints).
        authorization.TenantId = _currentActor.TenantId;
        authorization.CreatedBy = Actor;
        authorization.LastUpdatedBy = Actor;
        ClearReviewOutcome(authorization);

        // Persist via the operating-mode-selected backend. Replace = CHO-native
        // repository; Augment = configured external core. No vendor branching here.
        var created = await _backends.Resolve().CreateAsync(authorization, HttpContext.RequestAborted);

        _logger.LogInformation(
            "Authorization {AuthNumber} submitted successfully via {Backend} backend ({Mode} mode)",
            LogSanitizer.Sanitize(authorization.AuthorizationNumber), _backends.ActiveBackendKey, _backends.Mode);

        return CreatedAtAction(nameof(GetAuthorizationById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Removes every review-outcome field a submitter could pre-fill on the
    /// full-model create body.
    /// </summary>
    private static void ClearReviewOutcome(Authorization authorization)
    {
        authorization.ReviewDecision = null;
        authorization.ApprovedUnits = null;
        authorization.ApprovedServiceDateFrom = null;
        authorization.ApprovedServiceDateTo = null;
        authorization.ExpirationDate = null;
        authorization.DenialReasonCode = null;
        authorization.DenialReason = null;
        authorization.PendReason = null;
        authorization.FollowUpAction = null;
        authorization.ReviewerName = null;
        authorization.ReviewerPhone = null;
        authorization.ReviewedDate = null;
        authorization.EDI278ResponseControlNumber = null;
        authorization.RFAIReference = null;
        authorization.RFAIIssued = false;
        authorization.RFAIIssuedDate = null;
        authorization.RFAIResponseDate = null;
        authorization.SlaResumedAt = null;
        authorization.SlaEscalation = SlaEscalationLevel.None;
        authorization.SlaEscalatedAt = null;
        authorization.StatusHistory = new List<AuthorizationStatusChange>();

        foreach (var service in authorization.RequestedServices ?? new List<RequestedService>())
        {
            service.ApprovedUnits = null;
            service.ServiceStatus = null;
            service.DenialReason = null;
            service.DenialReasonCode = null;
            service.PendReason = null;
        }
    }

    /// <summary>
    /// Get authorization by ID
    /// </summary>
    [AuditRecordAccess("Authorization", RecordAccessActions.View, RecordIdRouteValue = "id")]
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> GetAuthorizationById(string id)
    {
        _logger.LogInformation("Fetching authorization by ID: {Id}", LogSanitizer.Sanitize(id));

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        return Ok(authorization);
    }

    /// <summary>
    /// Get authorization by authorization number
    /// </summary>
    [AuditRecordAccess("Authorization", RecordAccessActions.View, ResultIsRecord = true)]
    [HttpGet("number/{authNumber}")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> GetAuthorizationByNumber(string authNumber)
    {
        _logger.LogInformation("Fetching authorization by number: {AuthNumber}", LogSanitizer.Sanitize(authNumber));

        // Route through the operating-mode-selected backend so an Augment
        // deployment does not silently read from the CHO repository.
        var authorization = await _backends.Resolve().GetByNumberAsync(authNumber, HttpContext.RequestAborted);
        if (authorization == null)
        {
            return NotFound($"Authorization {authNumber} not found");
        }

        return Ok(authorization);
    }

    /// <summary>
    /// Check if authorization is valid for claim submission
    /// CRITICAL for claims processing: validates auth before submitting 837
    /// </summary>
    // Recorded by the action (against the authorization's id and member,
    // known only once it is loaded); a refusal is logged under the number.
    [AuditRecordAccess("Authorization", "Validate", RecordIdRouteValue = "authNumber", RecordedByAction = true)]
    [HttpGet("{authNumber}/validate")]
    [ProducesResponseType(typeof(AuthorizationValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthorizationValidationResponse>> ValidateAuthorization(
        string authNumber,
        [FromQuery] string? procedureCode = null,
        [FromQuery] DateTime? serviceDate = null,
        [FromQuery] string? providerNpi = null,
        [FromServices] RecordAccessWriter? accessLog = null,
        [FromQuery] int? lineNumber = null)
    {
        var checkDate = serviceDate ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Validating authorization {AuthNumber} for procedure {Procedure} on {Date}",
            LogSanitizer.Sanitize(authNumber), LogSanitizer.Sanitize(procedureCode), checkDate);

        var authorization = await _backends.Resolve().GetByNumberAsync(authNumber, HttpContext.RequestAborted);
        if (authorization == null)
        {
            await RecordValidateAsync(accessLog, authNumber, null, StatusCodes.Status404NotFound);
            return NotFound($"Authorization {authNumber} not found");
        }

        var providerApproved = IsProviderApprovedForAuthorization(authorization, providerNpi);
        if (lineNumber <= 0 || (lineNumber.HasValue && string.IsNullOrWhiteSpace(procedureCode)))
        {
            await RecordValidateAsync(accessLog, authorization.Id, authorization.MemberId, StatusCodes.Status400BadRequest);
            return BadRequest(new { error = "A positive line number requires a procedure code." });
        }

        var isValid = authorization.Status == AuthorizationStatus.Approved ||
                     authorization.Status == AuthorizationStatus.Modified;

        var isActive = checkDate >= authorization.ApprovedServiceDateFrom &&
                      (authorization.ExpirationDate == null || checkDate <= authorization.ExpirationDate) &&
                      (authorization.ApprovedServiceDateTo == null || checkDate <= authorization.ApprovedServiceDateTo);

        var ambiguousProcedure = false;
        var procedureApproved = true;
        decimal? approvedUnits = null;

        if (!string.IsNullOrEmpty(procedureCode))
        {
            var matchingServices = authorization.RequestedServices
                .Select((s, i) => new { Service = s, Number = s.LineNumber ?? i + 1 })
                .Where(s => s.Service.ProcedureCode == procedureCode && (!lineNumber.HasValue || s.Number == lineNumber))
                .Select(s => s.Service).ToList();
            ambiguousProcedure = matchingServices.Count > 1;
            var service = matchingServices.Count == 1 ? matchingServices[0] : null;

            if (service != null)
            {
                procedureApproved = service.ServiceStatus == "A1" || // Approved
                                   service.ServiceStatus == "A2";    // Modified
                approvedUnits = service.ApprovedUnits ?? service.RequestedUnits;
            }
            else
            {
                procedureApproved = false;
            }
        }


        var response = new AuthorizationValidationResponse
        {
            AuthorizationNumber = authorization.AuthorizationNumber,
            IsValid = isValid && isActive && procedureApproved && providerApproved,
            Status = authorization.Status,
            ApprovedServiceDateFrom = authorization.ApprovedServiceDateFrom,
            ApprovedServiceDateTo = authorization.ApprovedServiceDateTo,
            ExpirationDate = authorization.ExpirationDate,
            ApprovedUnits = approvedUnits,
            ValidationMessage = !isValid ? "Authorization not approved" :
                               !isActive ? "Authorization expired or not yet active" :
                               ambiguousProcedure ? "Procedure occurs on multiple lines; specify lineNumber" :
                               !procedureApproved ? $"Procedure {procedureCode} not approved" :
                               !providerApproved ? $"Provider {providerNpi} not approved for authorization" :
                               "Authorization valid"
        };

        await RecordValidateAsync(accessLog, authorization.Id, authorization.MemberId, StatusCodes.Status200OK);
        return Ok(response);
    }

    private Task RecordValidateAsync(RecordAccessWriter? accessLog, string recordId, string? memberId, int statusCode) =>
        accessLog is null
            ? Task.CompletedTask
            : accessLog.AppendAsync(HttpContext, _currentActor, new RecordAccessEvent
            {
                RecordType = "Authorization",
                RecordId = recordId,
                MemberId = string.IsNullOrWhiteSpace(memberId) ? null : memberId,
                Action = "Validate",
                Outcome = RecordAccessOutcomes.For(statusCode),
                StatusCode = statusCode,
            });

    private static bool IsProviderApprovedForAuthorization(Authorization authorization, string? providerNpi)
    {
        if (string.IsNullOrWhiteSpace(providerNpi))
        {
            return true;
        }

        var approvedProviderNpis = new[]
            {
                authorization.ServicingProviderNPI,
                authorization.RequestingProviderNPI
            }
            .Where(npi => !string.IsNullOrWhiteSpace(npi))
            .Select(npi => npi!.Trim())
            .ToArray();

        return approvedProviderNpis.Length == 0
            || approvedProviderNpis.Any(npi => string.Equals(
                npi,
                providerNpi.Trim(),
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Seed deterministic prior authorization fixtures for local validation.
    /// Development/Test hosts only. Fixtures carry review outcomes (approved
    /// authorizations), so seeding needs <c>authorizations:decide</c>; the
    /// tenant and the actor come from the token.
    /// </summary>
    [RequirePermission(DecidePermission)]
    [HttpPost("dev-seed")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(typeof(DevelopmentAuthorizationSeedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DevelopmentAuthorizationSeedResponse>> SeedDevelopmentAuthorizations(
        [FromBody] DevelopmentAuthorizationSeedRequest request)
    {
        if (!_environment.IsDevelopment() && !string.Equals(_environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (request?.Authorizations is not { Count: > 0 })
        {
            return BadRequest("At least one authorization fixture is required");
        }

        var now = DateTime.UtcNow;
        var created = 0;
        var updated = 0;

        foreach (var fixture in request.Authorizations)
        {
            if (string.IsNullOrWhiteSpace(fixture.AuthorizationNumber))
            {
                return BadRequest("Authorization fixtures must include authorizationNumber");
            }

            fixture.AuthorizationNumber = fixture.AuthorizationNumber.Trim();
            fixture.MemberId = fixture.MemberId?.Trim() ?? string.Empty;
            fixture.PatientFirstName = fixture.PatientFirstName?.Trim() ?? string.Empty;
            fixture.PatientLastName = fixture.PatientLastName?.Trim() ?? string.Empty;
            fixture.RequestingProviderNPI = fixture.RequestingProviderNPI?.Trim() ?? string.Empty;
            fixture.CreatedDate = fixture.CreatedDate == default ? now : fixture.CreatedDate;
            fixture.LastUpdatedDate = now;
            fixture.TenantId = _currentActor.TenantId;
            fixture.CreatedBy = Actor;
            fixture.LastUpdatedBy = Actor;

            var existing = await _authorizationRepository.GetByAuthorizationNumberAsync(fixture.AuthorizationNumber);
            if (existing is null)
            {
                fixture.Id = string.IsNullOrWhiteSpace(fixture.Id) ? Guid.NewGuid().ToString() : fixture.Id;
                await _authorizationRepository.CreateAsync(fixture);
                created++;
                continue;
            }

            fixture.Id = existing.Id;
            fixture.CreatedDate = existing.CreatedDate == default ? fixture.CreatedDate : existing.CreatedDate;
            fixture.CreatedBy = existing.CreatedBy ?? Actor;
            // The fixture replaces the stored document as read just now.
            fixture.Revision = existing.Revision;
            await _authorizationRepository.UpdateAsync(fixture);
            updated++;
        }

        _logger.LogInformation(
            "Seeded {Total} development authorization fixtures ({Created} created, {Updated} updated)",
            created + updated, created, updated);

        return Ok(new DevelopmentAuthorizationSeedResponse(created + updated, created, updated));
    }

    /// <summary>
    /// Search authorizations (by member, provider, status, date range)
    /// </summary>
    [AuditRecordAccess("Authorization", RecordAccessActions.Search)]
    [HttpGet("search")]
    [ProducesResponseType(typeof(IEnumerable<Authorization>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Authorization>>> SearchAuthorizations(
        [FromQuery] string? memberId = null,
        [FromQuery] string? providerNPI = null,
        [FromQuery] DateTime? serviceDateFrom = null,
        [FromQuery] DateTime? serviceDateTo = null,
        [FromQuery] AuthorizationStatus? status = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        _logger.LogInformation(
            "Searching authorizations: member={Member}, provider={Provider}, dateFrom={From}, dateTo={To}, status={Status}, lob={LOB}",
            LogSanitizer.MaskId(memberId), LogSanitizer.Sanitize(providerNPI), serviceDateFrom, serviceDateTo, status, lineOfBusiness);

        var authorizations = await _authorizationRepository.SearchAsync(
            memberId, providerNPI, serviceDateFrom, serviceDateTo, status, lineOfBusiness, page, pageSize);

        return Ok(authorizations);
    }

    /// <summary>
    /// Update authorization status (278 response processing)
    /// </summary>
    [RequirePermission(DecidePermission)]
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> UpdateAuthorizationStatus(
        string id,
        [FromBody] AuthorizationStatusUpdate statusUpdate)
    {
        _logger.LogInformation(
            "Updating authorization {Id} status to {Status}",
            LogSanitizer.Sanitize(id), statusUpdate.Status);

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        statusUpdate.ReviewDecision ??= AuthorizationDecisionPolicy.DecisionFor(statusUpdate.Status);
        try { AuthorizationDecisionPolicy.Prepare(authorization, statusUpdate.Status, statusUpdate.ReviewDecision, statusUpdate); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        authorization.Status = statusUpdate.Status;
        authorization.ReviewDecision = statusUpdate.ReviewDecision;
        authorization.LastUpdatedDate = DateTime.UtcNow;
        authorization.LastUpdatedBy = Actor;

        if (statusUpdate.Status == AuthorizationStatus.Approved ||
            statusUpdate.Status == AuthorizationStatus.Modified ||
            statusUpdate.Status == AuthorizationStatus.Denied)
        {
            authorization.ReviewedDate = DateTime.UtcNow;
        }

        if (!string.IsNullOrEmpty(statusUpdate.Notes))
        {
            authorization.Notes = string.IsNullOrEmpty(authorization.Notes)
                ? statusUpdate.Notes
                : $"{authorization.Notes}\n{DateTime.UtcNow:yyyy-MM-dd HH:mm}: {statusUpdate.Notes}";
        }

        // Persist the transition through the selected backend so the status
        // history is appended and an Augment deployment does not silently write
        // to the CHO repository.
        var updated = await _backends.Resolve().UpdateStatusAsync(
            authorization, authorization.Status, authorization.ReviewDecision,
            authorization.DenialReason, HttpContext.RequestAborted, statusUpdate);

        // A status update that lands on pended-for-information raises the request
        // too. This is also the repair path: an authorization left Pended/A4 with
        // no request (because rfai-service was unreachable at decision time) gets
        // one here, under the same correlation key, so the retry cannot duplicate.
        await EnsureAdditionalInformationRequestAsync(
            updated, statusUpdate.RequestedInformation, statusUpdate.InformationDueDate,
            decisionControlNumber: null);

        return Ok(updated);
    }

    /// <summary>
    /// Process 278 response (approval/denial/pend decision)
    /// </summary>
    [RequirePermission(DecidePermission)]
    [HttpPost("{id}/response")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> ProcessAuthorizationResponse(
        string id,
        [FromBody] AuthorizationResponse response)
    {
        _logger.LogInformation(
            "Processing 278 response for authorization {Id}, decision={Decision}",
            LogSanitizer.Sanitize(id), LogSanitizer.Sanitize(response.ReviewDecision));

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        var decisionStatus = response.ReviewDecision switch
        {
            "A1" => AuthorizationStatus.Approved, "A2" => AuthorizationStatus.Modified,
            "A3" => AuthorizationStatus.Denied, "A4" => AuthorizationStatus.Pended,
            _ => (AuthorizationStatus?)null,
        };
        if (!decisionStatus.HasValue) return BadRequest(new { error = "Review decision must be A1, A2, A3 or A4." });
        try { AuthorizationDecisionPolicy.Prepare(authorization, decisionStatus.Value, response.ReviewDecision, response); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        authorization.EDI278ResponseControlNumber = response.ControlNumber;
        authorization.ReviewDecision = response.ReviewDecision;
        authorization.ReviewedDate = DateTime.UtcNow;
        authorization.LastUpdatedDate = DateTime.UtcNow;
        authorization.LastUpdatedBy = Actor;

        // Map review decision to status
        authorization.Status = response.ReviewDecision switch
        {
            "A1" => AuthorizationStatus.Approved,
            "A2" => AuthorizationStatus.Modified,
            "A3" => AuthorizationStatus.Denied,
            "A4" => AuthorizationStatus.Pended,
            _ => AuthorizationStatus.InReview
        };

        if (response.ApprovedUnits.HasValue)
        {
            authorization.ApprovedUnits = response.ApprovedUnits.Value;
        }

        if (response.ApprovedServiceDateFrom.HasValue)
        {
            authorization.ApprovedServiceDateFrom = response.ApprovedServiceDateFrom.Value;
            authorization.ApprovedServiceDateTo = response.ApprovedServiceDateTo;
        }

        if (response.ExpirationDate.HasValue)
        {
            authorization.ExpirationDate = response.ExpirationDate.Value;
        }

        if (!string.IsNullOrEmpty(response.DenialReasonCode))
        {
            authorization.DenialReasonCode = response.DenialReasonCode;
            authorization.DenialReason = response.DenialReason;
        }

        if (!string.IsNullOrEmpty(response.PendReason))
        {
            authorization.PendReason = response.PendReason;
            authorization.FollowUpAction = response.FollowUpAction;
        }

        // 278 reviewer CONTACT details for the provider (PER segment). They do
        // not identify who recorded the decision: that is LastUpdatedBy (token).
        authorization.ReviewerName = response.ReviewerName;
        authorization.ReviewerPhone = response.ReviewerPhone;

        // Persist the decision through the selected backend (history append +
        // no silent CHO write in Augment).
        var updated = await _backends.Resolve().UpdateStatusAsync(
            authorization, authorization.Status, authorization.ReviewDecision,
            authorization.DenialReason, HttpContext.RequestAborted, response);

        // An A4 decision that names what it needs raises the provider-facing
        // documentation request. The decision is already durable at this point:
        // if raising the request fails, the pend stands and the retry is
        // idempotent — see PendedAuthorizationRfaiCoordinator.
        await EnsureAdditionalInformationRequestAsync(
            updated, response.RequestedInformation, response.InformationDueDate,
            response.ControlNumber);

        return Ok(updated);
    }

    /// <summary>
    /// Raises the CDex additional-information request an A4 decision implies, and
    /// persists the handle on the authorization.
    ///
    /// The handle write is a SECOND write, deliberately: the decision must land
    /// whether or not rfai-service is reachable, and stamping the handle
    /// afterwards keeps a failure here from rolling back a recorded review
    /// decision. Failing to stamp leaves the authorization pended with
    /// RFAIIssued false, which the coordinator documents as the recoverable
    /// state.
    /// </summary>
    private async System.Threading.Tasks.Task EnsureAdditionalInformationRequestAsync(
        Authorization authorization,
        List<RequestedInformationItem> requestedInformation,
        DateTime? dueDate,
        string? decisionControlNumber)
    {
        if (_rfai is null) return;

        var stamped = await _rfai.EnsureRequestForDecisionAsync(
            authorization, requestedInformation, dueDate, decisionControlNumber,
            HttpContext.RequestAborted);

        if (!stamped) return;

        try
        {
            await _authorizationRepository.UpdateAsync(authorization);
        }
        catch (Exception ex)
        {
            // The request itself exists and is durable in rfai-service; only the
            // handle on the authorization is missing. A later delivery of the
            // same decision replays onto the same request and stamps it again.
            _logger.LogError(ex,
                "Authorization {AuthNumber}: the additional-information request was raised "
                + "but its handle could not be persisted.",
                LogSanitizer.Sanitize(authorization.AuthorizationNumber));
        }
    }

    /// <summary>
    /// Get authorizations summary statistics
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AuthorizationsSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthorizationsSummary>> GetAuthorizationsSummary(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        var fromDate = from ?? DateTime.UtcNow.AddMonths(-1);
        var toDate = to ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Fetching authorizations summary from {From} to {To}, lob={LOB}",
            fromDate, toDate, lineOfBusiness);

        var summary = await _authorizationRepository.GetAuthorizationsSummaryAsync(fromDate, toDate, lineOfBusiness);
        return Ok(summary);
    }

    /// <summary>
    /// Authorization Insights (portal-navigation.md §5.1): the CMS-0057
    /// reporting figures for requests submitted in [from, to] (volume,
    /// approval and denial rates, decision timeliness standard vs expedited),
    /// the review queue now by status and SLA escalation, top denial reasons,
    /// and approved authorizations whose service period ended with no claim
    /// yet (claim usage from claims-service, with the caller's token). Aggregates only; each figure's authorizations are listed by
    /// <c>insights/list</c> with the same criteria.
    /// </summary>
    [HttpGet("insights")]
    [ProducesResponseType(typeof(AuthorizationInsights), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AuthorizationInsights>> GetAuthorizationInsights(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromServices] AuthorizationService.Services.Insights.UnusedAuthorizations unusedAuthorizations = null!)
    {
        if (InsightsPeriod(from, to) is not { } period) return BadRequest(new { error = "from must be before to, at most a year apart" });

        var now = DateTime.UtcNow;
        var activity = await _authorizationRepository.ListActivityBetweenAsync(period.From, period.To);
        var open = await _authorizationRepository.GetOpenAuthorizationsAsync(_currentActor.TenantId);
        var unused = await unusedAuthorizations.ComputeAsync(period.From, period.To, lineOfBusiness, now, HttpContext.RequestAborted);
        var insights = AuthorizationInsightSets.Build(activity, open, period.From, period.To, lineOfBusiness, now);
        return Ok(new AuthorizationInsights
        {
            AsOf = insights.AsOf, From = insights.From, To = insights.To, LineOfBusiness = insights.LineOfBusiness,
            Reporting = insights.Reporting, Queue = insights.Queue, DenialReasons = insights.DenialReasons,
            Unused = new AuthorizationUnusedInsight
            {
                Ended = unused.Ended.Count, WithoutClaim = unused.Unused?.Count, UnavailableReason = unused.UnavailableReason,
            },
        });
    }

    /// <summary>
    /// The authorizations behind one Insights figure (newest first, at most
    /// <paramref name="limit"/>): the drill-down. Logged in the access log.
    /// </summary>
    [AuditRecordAccess("Authorization", RecordAccessActions.List)]
    [HttpGet("insights/list")]
    [ProducesResponseType(typeof(IEnumerable<Authorization>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IEnumerable<Authorization>>> ListAuthorizationInsight(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null,
        [FromQuery] string scope = AuthorizationInsightSets.PeriodScope,
        [FromQuery] string? requestType = null,
        [FromQuery] string? outcome = null,
        [FromQuery] bool? withinTimeframe = null,
        [FromQuery] bool? automated = null,
        [FromQuery] string? denialReasonCode = null,
        [FromQuery] AuthorizationStatus? status = null,
        [FromQuery] SlaEscalationLevel? sla = null,
        [FromQuery] int limit = 500,
        [FromServices] AuthorizationService.Services.Insights.UnusedAuthorizations unusedAuthorizations = null!)
    {
        if (scope is not (AuthorizationInsightSets.PeriodScope or AuthorizationInsightSets.QueueScope or AuthorizationInsightSets.UnusedScope))
            return BadRequest(new { error = "scope is period, queue or unused" });
        if (InsightsPeriod(from, to) is not { } period) return BadRequest(new { error = "from must be before to, at most a year apart" });

        if (scope == AuthorizationInsightSets.UnusedScope)
        {
            var result = await unusedAuthorizations.ComputeAsync(period.From, period.To, lineOfBusiness, DateTime.UtcNow, HttpContext.RequestAborted);
            if (result.Unused is null)
                return Problem(title: "Claim usage couldn't be checked", detail: result.UnavailableReason, statusCode: StatusCodes.Status503ServiceUnavailable);
            return Ok(result.Unused.OrderByDescending(a => a.ApprovedServiceDateTo ?? a.ExpirationDate).Take(Math.Clamp(limit, 1, 1000)).ToList());
        }

        var criteria = new AuthorizationInsightCriteria
        {
            Scope = scope, RequestType = requestType, Outcome = outcome, WithinTimeframe = withinTimeframe,
            Automated = automated, DenialReasonCode = denialReasonCode, Status = status, Sla = sla,
        };
        var activity = scope == AuthorizationInsightSets.PeriodScope
            ? await _authorizationRepository.ListActivityBetweenAsync(period.From, period.To)
            : [];
        var open = scope == AuthorizationInsightSets.QueueScope
            ? await _authorizationRepository.GetOpenAuthorizationsAsync(_currentActor.TenantId)
            : [];
        return Ok(AuthorizationInsightSets.List(criteria, activity, open, period.From, period.To, lineOfBusiness, limit));
    }

    private static (DateTime From, DateTime To)? InsightsPeriod(DateTime? from, DateTime? to)
    {
        var toDate = (to ?? DateTime.UtcNow).ToUniversalTime();
        var fromDate = (from ?? toDate.AddDays(-30)).ToUniversalTime();
        return fromDate < toDate && toDate - fromDate <= TimeSpan.FromDays(366) ? (fromDate, toDate) : null;
    }

    /// <summary>
    /// CMS-0057-F public-reporting metrics for requests submitted in the period
    /// (default: the current calendar year to date): approved and denied
    /// percentages and average and median decision time, for standard and
    /// expedited requests, plus the metrics the data can't support yet.
    /// </summary>
    [HttpGet("cms-0057/metrics")]
    [ProducesResponseType(typeof(Cms0057Metrics), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Cms0057Metrics>> GetCms0057Metrics(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] LineOfBusiness? lineOfBusiness = null)
    {
        var now = DateTime.UtcNow;
        var fromDate = from ?? new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var toDate = to ?? now;
        if (fromDate > toDate)
            return BadRequest(new { error = "from must be on or before to" });

        var authorizations = await _authorizationRepository.ListActivityBetweenAsync(fromDate, toDate);
        return Ok(Cms0057Reporting.ComputeMetrics(authorizations, fromDate, toDate, lineOfBusiness));
    }

    /// <summary>
    /// The most recent prior-authorization decisions (default: last 30 days, up
    /// to 100): decision time against the 72-hour or 7-day timeframe, whether an
    /// automated rule or a reviewer decided, and the reason.
    /// </summary>
    [AuditRecordAccess("Authorization", RecordAccessActions.List)]
    [HttpGet("cms-0057/decisions")]
    [ProducesResponseType(typeof(IEnumerable<Cms0057Decision>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<Cms0057Decision>>> GetCms0057Decisions(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 100)
    {
        days = Math.Clamp(days, 1, 366);
        limit = Math.Clamp(limit, 1, 500);
        var now = DateTime.UtcNow;
        var since = now.AddDays(-days);

        var authorizations = await _authorizationRepository.ListActivityBetweenAsync(since, now);
        return Ok(Cms0057Reporting.RecentDecisions(authorizations, since, limit));
    }

    /// <summary>
    /// Get authorizations approaching or past their SLA deadline
    /// </summary>
    [AuditRecordAccess("Authorization", RecordAccessActions.List)]
    [HttpGet("sla/at-risk")]
    [ProducesResponseType(typeof(IEnumerable<AuthorizationSlaStatus>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AuthorizationSlaStatus>>> GetAtRiskAuthorizations(
        [FromQuery] SlaEscalationLevel? minLevel = SlaEscalationLevel.Warning)
    {
        // Tenant from the token only. This endpoint used to take ?tenantId=,
        // which let any caller list another tenant's open authorizations.
        var auths = await _authorizationRepository.GetOpenAuthorizationsAsync(_currentActor.TenantId);

        var effectiveMinLevel = minLevel ?? SlaEscalationLevel.Warning;

        var atRisk = auths
            .Select(a => SlaWatchdogService.ComputeSlaStatus(a))
            .Where(s => s.EscalationLevel >= effectiveMinLevel)
            .OrderBy(s => s.HoursRemaining)
            .ToList();

        return Ok(atRisk);
    }

    /// <summary>
    /// Cancel authorization
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelAuthorization(string id)
    {
        _logger.LogInformation("Cancelling authorization: {Id}", LogSanitizer.Sanitize(id));

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        authorization.Status = AuthorizationStatus.Cancelled;
        authorization.LastUpdatedDate = DateTime.UtcNow;
        authorization.LastUpdatedBy = Actor;

        await _authorizationRepository.UpdateAsync(authorization);

        return NoContent();
    }
}

public sealed record DevelopmentAuthorizationSeedRequest(List<Authorization> Authorizations);

public sealed record DevelopmentAuthorizationSeedResponse(int Total, int Created, int Updated);
