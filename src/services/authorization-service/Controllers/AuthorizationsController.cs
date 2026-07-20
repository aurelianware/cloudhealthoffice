using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AuthorizationService.Middleware;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;

namespace AuthorizationService.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class AuthorizationsController : ControllerBase
{
    private readonly IAuthorizationRepository _authorizationRepository;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AuthorizationsController> _logger;

    public AuthorizationsController(
        IAuthorizationRepository authorizationRepository,
        IWebHostEnvironment environment,
        ILogger<AuthorizationsController> logger)
    {
        _authorizationRepository = authorizationRepository;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>
    /// Submit new prior authorization request (278 request)
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<Authorization>> SubmitAuthorization([FromBody] Authorization authorization)
    {
        _logger.LogInformation(
            "Submitting authorization for member {MemberId}, provider {ProviderNPI}, service date {ServiceDate}",
            SanitizeForLog(authorization.MemberId), SanitizeForLog(authorization.RequestingProviderNPI), authorization.RequestedServiceDateFrom);

        // Validate authorization
        if (authorization.RequestedServices.Count == 0)
        {
            return BadRequest("Authorization must have at least one requested service");
        }

        authorization.Id = Guid.NewGuid().ToString();
        authorization.Status = AuthorizationStatus.Submitted;
        authorization.SubmittedDate = DateTime.UtcNow;
        authorization.CreatedDate = DateTime.UtcNow;
        authorization.LastUpdatedDate = DateTime.UtcNow;

        var created = await _authorizationRepository.CreateAsync(authorization);

        _logger.LogInformation("Authorization {AuthNumber} submitted successfully", SanitizeForLog(authorization.AuthorizationNumber));

        return CreatedAtAction(nameof(GetAuthorizationById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Get authorization by ID
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> GetAuthorizationById(string id)
    {
        _logger.LogInformation("Fetching authorization by ID: {Id}", SanitizeForLog(id));

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
    [HttpGet("number/{authNumber}")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> GetAuthorizationByNumber(string authNumber)
    {
        _logger.LogInformation("Fetching authorization by number: {AuthNumber}", SanitizeForLog(authNumber));

        var authorization = await _authorizationRepository.GetByAuthorizationNumberAsync(authNumber);
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
    [AllowAnonymous]
    [HttpGet("{authNumber}/validate")]
    [ProducesResponseType(typeof(AuthorizationValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuthorizationValidationResponse>> ValidateAuthorization(
        string authNumber,
        [FromQuery] string? procedureCode = null,
        [FromQuery] DateTime? serviceDate = null,
        [FromQuery] string? providerNpi = null)
    {
        if (!AllowsAnonymousLocalValidation() && HttpContext?.User.Identity?.IsAuthenticated != true)
        {
            return Forbid();
        }

        var checkDate = serviceDate ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Validating authorization {AuthNumber} for procedure {Procedure} on {Date}",
            SanitizeForLog(authNumber), SanitizeForLog(procedureCode), checkDate);

        var authorization = await _authorizationRepository.GetByAuthorizationNumberAsync(authNumber);
        if (authorization == null)
        {
            return NotFound($"Authorization {authNumber} not found");
        }

        var isValid = authorization.Status == AuthorizationStatus.Approved ||
                     authorization.Status == AuthorizationStatus.Modified;

        var isActive = checkDate >= authorization.ApprovedServiceDateFrom &&
                      (authorization.ExpirationDate == null || checkDate <= authorization.ExpirationDate);

        var procedureApproved = true;
        decimal? approvedUnits = null;

        if (!string.IsNullOrEmpty(procedureCode))
        {
            var service = authorization.RequestedServices
                .FirstOrDefault(s => s.ProcedureCode == procedureCode);

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

        var providerApproved = IsProviderApprovedForAuthorization(authorization, providerNpi);

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
                               !procedureApproved ? $"Procedure {procedureCode} not approved" :
                               !providerApproved ? $"Provider {providerNpi} not approved for authorization" :
                               "Authorization valid"
        };

        return Ok(response);
    }

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

    private bool AllowsAnonymousLocalValidation() =>
        _environment.IsDevelopment() ||
        string.Equals(_environment.EnvironmentName, "Test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Seed deterministic prior authorization fixtures for local validation.
    /// </summary>
    [AllowAnonymous]
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
            SanitizeForLog(memberId), SanitizeForLog(providerNPI), serviceDateFrom, serviceDateTo, status, lineOfBusiness);

        var authorizations = await _authorizationRepository.SearchAsync(
            memberId, providerNPI, serviceDateFrom, serviceDateTo, status, lineOfBusiness, page, pageSize);

        return Ok(authorizations);
    }

    /// <summary>
    /// Update authorization status (278 response processing)
    /// </summary>
    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> UpdateAuthorizationStatus(
        string id,
        [FromBody] AuthorizationStatusUpdate statusUpdate)
    {
        _logger.LogInformation(
            "Updating authorization {Id} status to {Status}",
            SanitizeForLog(id), statusUpdate.Status);

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        authorization.Status = statusUpdate.Status;
        authorization.ReviewDecision = statusUpdate.ReviewDecision;
        authorization.LastUpdatedDate = DateTime.UtcNow;

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

        var updated = await _authorizationRepository.UpdateAsync(authorization);
        return Ok(updated);
    }

    /// <summary>
    /// Process 278 response (approval/denial/pend decision)
    /// </summary>
    [HttpPost("{id}/response")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> ProcessAuthorizationResponse(
        string id,
        [FromBody] AuthorizationResponse response)
    {
        _logger.LogInformation(
            "Processing 278 response for authorization {Id}, decision={Decision}",
            SanitizeForLog(id), SanitizeForLog(response.ReviewDecision));

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        authorization.EDI278ResponseControlNumber = response.ControlNumber;
        authorization.ReviewDecision = response.ReviewDecision;
        authorization.ReviewedDate = DateTime.UtcNow;
        authorization.LastUpdatedDate = DateTime.UtcNow;

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

        authorization.ReviewerName = response.ReviewerName;
        authorization.ReviewerPhone = response.ReviewerPhone;

        var updated = await _authorizationRepository.UpdateAsync(authorization);
        return Ok(updated);
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
    /// Get authorizations approaching or past their SLA deadline
    /// </summary>
    [HttpGet("sla/at-risk")]
    [ProducesResponseType(typeof(IEnumerable<AuthorizationSlaStatus>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<AuthorizationSlaStatus>>> GetAtRiskAuthorizations(
        [FromQuery] SlaEscalationLevel? minLevel = SlaEscalationLevel.Warning,
        [FromQuery] string? tenantId = null)
    {
        var auths = await _authorizationRepository.GetOpenAuthorizationsAsync(tenantId);

        var effectiveMinLevel = minLevel ?? SlaEscalationLevel.Warning;

        var atRisk = auths
            .Select(SlaWatchdogService.ComputeSlaStatus)
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
        _logger.LogInformation("Cancelling authorization: {Id}", SanitizeForLog(id));

        var authorization = await _authorizationRepository.GetByIdAsync(id);
        if (authorization == null)
        {
            return NotFound($"Authorization {id} not found");
        }

        authorization.Status = AuthorizationStatus.Cancelled;
        authorization.LastUpdatedDate = DateTime.UtcNow;

        await _authorizationRepository.UpdateAsync(authorization);

        return NoContent();
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
    }
}

public sealed record DevelopmentAuthorizationSeedRequest(List<Authorization> Authorizations);

public sealed record DevelopmentAuthorizationSeedResponse(int Total, int Created, int Updated);
