using AuthorizationService.Exceptions;
using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace AuthorizationService.Controllers;

/// <summary>
/// Internal: records a prior-authorization request together with the decision
/// an automated adjudicator already made on it (D-02). The only caller is
/// fhir-service's PAS <c>Claim/$submit</c>, which adjudicates synchronously and
/// must not issue a <c>preAuthRef</c> until the record exists.
///
/// This is deliberately NOT <c>POST api/authorizations</c>. The public create
/// forces Submitted and clears every review-outcome field from the body,
/// because an external caller must not be able to approve its own request;
/// that control stays. Here the outcome is accepted because of who is calling:
/// <list type="bullet">
///   <item>Only fhir-service's service token is accepted
///   (<see cref="RequireServiceClientAttribute"/>: the <c>cho.service</c> role
///   from an issuer trusted to mint it, with <c>sub</c> = <c>azp</c> =
///   <c>fhir-service</c>). User tokens, whatever their permissions, other
///   services, workloads and SMART tokens get 401/403.</item>
///   <item>The tenant is the service token's tenant. fhir-service mints that
///   token for the tenant of its own authenticated caller.</item>
///   <item>The record is attributed to the service identity (<c>CreatedBy</c>,
///   every history entry), and the rule that decided is kept as the history
///   entry's <c>Source</c>. Nothing in the body names an actor.</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/internal/authorizations/automated-decisions")]
[RequireServiceClient(FhirServiceClientId)]
[ApiExplorerSettings(IgnoreApi = true)]
[Produces("application/json")]
public sealed class AutomatedDecisionsController : ControllerBase
{
    /// <summary>fhir-service's service client id (token-service registration and <c>ChoAuth:ServiceToken:ClientId</c>).</summary>
    public const string FhirServiceClientId = "fhir-service";

    /// <summary>How far ahead of this host's clock a caller's timestamps may be.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How old a receipt may be. PAS answers synchronously (15 s), so a
    /// receipt older than this is not a request being recorded now and would
    /// backdate the decision clock.
    /// </summary>
    private static readonly TimeSpan MaxReceiptAge = TimeSpan.FromHours(1);

    private readonly IAuthorizationBackendSelector _backends;
    private readonly ICurrentActor _actor;
    private readonly ILogger<AutomatedDecisionsController> _logger;

    public AutomatedDecisionsController(
        IAuthorizationBackendSelector backends,
        ICurrentActor actor,
        ILogger<AutomatedDecisionsController> logger)
    {
        _backends = backends;
        _actor = actor;
        _logger = logger;
    }

    [HttpPost]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Authorization>> Record([FromBody] AutomatedDecisionRecordRequest request)
    {
        var problem = Validate(request, DateTime.UtcNow);
        if (problem is not null)
            return ValidationProblem(problem);

        var backend = _backends.Resolve();
        var number = request.AuthorizationNumber.Trim();

        // Tenant-scoped by the repository (tenant from the token).
        if (await backend.GetByNumberAsync(number, HttpContext.RequestAborted) is { } existing)
        {
            // The same request again (fhir-service lost the response to a
            // write that succeeded): the answer is the record already stored,
            // not a second one. Anything else under this number is a conflict.
            if (existing.RequestHash is not null
                && string.Equals(existing.RequestHash, request.RequestHash, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Automated decision for authorization {AuthNumber} in tenant {Tenant} already recorded; returning it",
                    LogSanitizer.Sanitize(number), LogSanitizer.Sanitize(_actor.TenantId));
                return Ok(existing);
            }

            _logger.LogWarning(
                "Automated decision refused: authorization {AuthNumber} already exists in tenant {Tenant}",
                LogSanitizer.Sanitize(number), LogSanitizer.Sanitize(_actor.TenantId));
            return Conflict($"Authorization {number} already exists.");
        }

        var now = DateTime.UtcNow;
        var authorization = new Authorization
        {
            Id = Guid.NewGuid().ToString(),
            TenantId = _actor.TenantId,
            AuthorizationNumber = number,
            RequestHash = request.RequestHash,
            MemberId = request.MemberId.Trim(),
            PatientReference = request.PatientReference,
            CoverageId = request.CoverageId,
            PatientFirstName = request.PatientFirstName.Trim(),
            PatientLastName = request.PatientLastName.Trim(),
            PatientDateOfBirth = request.PatientDateOfBirth!.Value,
            LineOfBusiness = request.LineOfBusiness,
            RequestingProviderNPI = request.RequestingProviderNPI,
            RequestingProviderName = request.RequestingProviderName,
            ServicingProviderNPI = request.ServicingProviderNPI,
            ServicingProviderName = request.ServicingProviderName,
            AuthorizationType = AuthorizationType.PreAuthorization,
            CertificationType = "I",
            ServiceTypeCode = request.ServiceTypeCode,
            LevelOfService = request.LevelOfService,
            RequestedServiceDateFrom = request.RequestedServiceDateFrom!.Value,
            RequestedServiceDateTo = request.RequestedServiceDateTo,
            DiagnosisCodes = request.DiagnosisCodes ?? new List<DiagnosisCode>(),
            // Ignore caller-supplied outcomes; the backend applies the trusted decision to every line.
            RequestedServices = request.RequestedServices.Select(s => new RequestedService
            {
                LineNumber = s.LineNumber,
                ProcedureCode = s.ProcedureCode,
                ProductOrServiceSystem = s.ProductOrServiceSystem,
                ProcedureDescription = s.ProcedureDescription,
                Modifiers = s.Modifiers ?? new List<string>(),
                RequestedUnits = s.RequestedUnits,
                UnitType = s.UnitType,
                UnitCode = s.UnitCode,
                UnitSystem = s.UnitSystem,
                PlaceOfServiceCode = s.PlaceOfServiceCode,
                RevenueCode = s.RevenueCode,
            }).ToList(),
            Status = AuthorizationStatus.Submitted,
            SubmittedDate = request.ReceivedAt!.Value,
            CreatedDate = now,
            LastUpdatedDate = now,
            CreatedBy = _actor.UserId,
            LastUpdatedBy = _actor.UserId,
        };

        Authorization stored;
        try
        {
            stored = await backend.RecordAutomatedDecisionAsync(
                authorization, request.Decision!, HttpContext.RequestAborted);
        }
        catch (DuplicateAuthorizationNumberException)
        {
            // Another writer passed the pre-check before this insert. The unique
            // tenant/number index selects the winner; a matching hash is a retry.
            var winner = await backend.GetByNumberAsync(number, HttpContext.RequestAborted);
            if (winner?.RequestHash is not null
                && string.Equals(winner.RequestHash, request.RequestHash, StringComparison.Ordinal))
                return Ok(winner);
            return Conflict($"Authorization {number} already exists.");
        }

        _logger.LogInformation(
            "Automated decision recorded: authorization {AuthNumber} tenant {Tenant} status {Status} by {Actor}",
            LogSanitizer.Sanitize(stored.AuthorizationNumber), LogSanitizer.Sanitize(stored.TenantId), stored.Status,
            LogSanitizer.Sanitize(_actor.UserId));

        return StatusCode(StatusCodes.Status201Created, stored);
    }

    /// <summary>
    /// One record this endpoint stored, by number, in the token's tenant. For
    /// fhir-service's compensating read when a write's answer was lost: it
    /// compares the stored <see cref="Authorization.RequestHash"/> with its
    /// own before answering the provider from the record.
    /// </summary>
    [HttpGet("{authorizationNumber}")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> Get(string authorizationNumber)
    {
        if (!AuthorizationNumberFormat.IsMatch(authorizationNumber))
            return BadRequest("Malformed authorization number.");

        var stored = await _backends.Resolve().GetByNumberAsync(authorizationNumber, HttpContext.RequestAborted);
        return stored is null ? NotFound() : Ok(stored);
    }

    // \z, not $: $ also matches before a trailing newline. (The body's
    // [RegularExpression] already requires the match to be the whole value.)
    private static readonly Regex AuthorizationNumberFormat =
        new(@"^[A-Za-z0-9-]{1,50}\z", RegexOptions.Compiled);

    /// <summary>Checks the attributes cannot express. Null when the request is consistent.</summary>
    internal static ValidationProblemDetails? Validate(AutomatedDecisionRecordRequest request, DateTime now)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string key, string message) => errors[key] = [message];

        var decision = request.Decision!;
        var received = request.ReceivedAt!.Value;
        var decided = decision.DecidedAt!.Value;

        if (received > now + ClockSkew)
            Add(nameof(request.ReceivedAt), "ReceivedAt is in the future.");
        else if (received < now - MaxReceiptAge)
            Add(nameof(request.ReceivedAt), "ReceivedAt is too old to be recorded as an automated decision.");

        if (decided < received)
            Add("Decision.DecidedAt", "DecidedAt is before ReceivedAt.");
        else if (decided > now + ClockSkew)
            Add("Decision.DecidedAt", "DecidedAt is in the future.");

        // A no-evaluation rule reaches only its own outcome (an exclusion
        // denies; the others pend), whatever else the request says.
        var boundOutcome = default(AutomatedDecisionOutcome);
        var noEvaluationRule = decision.Rule is not null
            && AutomatedDecision.NoEvaluationRuleOutcomes.TryGetValue(decision.Rule, out boundOutcome);
        if (noEvaluationRule && decision.Outcome != boundOutcome)
            Add("Decision.Outcome", $"Rule {decision.Rule} can only be recorded as {boundOutcome}.");

        // The no-evaluation pends read nothing: any LOB or state on them,
        // even the record's own LOB, would be stored as a basis not used.
        // (A provider exclusion keeps the general rule below.)
        if (noEvaluationRule && boundOutcome == AutomatedDecisionOutcome.Pended
            && (decision.LineOfBusiness is not null || decision.StateCode is not null))
        {
            Add("Decision.LineOfBusiness",
                $"Rule {decision.Rule} evaluates nothing, so it carries no line of business or state.");
        }
        else if (decision.LineOfBusiness is null)
        {
            if (!noEvaluationRule)
                Add("Decision.LineOfBusiness",
                    "The decision's line of business is required, except for a rule that evaluates none.");
        }
        else if (decision.LineOfBusiness != request.LineOfBusiness)
        {
            Add("Decision.LineOfBusiness",
                "The decision's line of business must be the record's: the record may not state a basis the decision did not use.");
        }

        if (request.RequestedServiceDateTo < request.RequestedServiceDateFrom)
            Add(nameof(request.RequestedServiceDateTo), "RequestedServiceDateTo is before RequestedServiceDateFrom.");

        var hasApprovedPeriod = decision.ApprovedServiceDateFrom.HasValue || decision.ApprovedServiceDateTo.HasValue;
        switch (decision.Outcome)
        {
            case AutomatedDecisionOutcome.Approved:
                if (!decision.ApprovedServiceDateFrom.HasValue)
                    Add("Decision.ApprovedServiceDateFrom", "An approval needs an approved period.");
                else if (decision.ApprovedServiceDateTo < decision.ApprovedServiceDateFrom)
                    Add("Decision.ApprovedServiceDateTo", "ApprovedServiceDateTo is before ApprovedServiceDateFrom.");
                break;
            case AutomatedDecisionOutcome.Denied:
            case AutomatedDecisionOutcome.Pended:
                if (hasApprovedPeriod)
                    Add("Decision.ApprovedServiceDateFrom", "Only an approval carries an approved period.");
                break;
            default:
                Add("Decision.Outcome", "Unknown outcome.");
                break;
        }

        return errors.Count == 0 ? null : new ValidationProblemDetails(errors);
    }
}
