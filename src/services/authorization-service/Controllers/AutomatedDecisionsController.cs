using AuthorizationService.Exceptions;
using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Rfai;
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
    private readonly IPendedAuthorizationRfaiCoordinator? _rfai;
    private readonly IAuthorizationRepository? _repository;

    public AutomatedDecisionsController(
        IAuthorizationBackendSelector backends,
        ICurrentActor actor,
        ILogger<AutomatedDecisionsController> logger,
        IPendedAuthorizationRfaiCoordinator? rfai = null,
        IAuthorizationRepository? repository = null)
    {
        _backends = backends;
        _actor = actor;
        _logger = logger;
        _rfai = rfai;
        _repository = repository;
    }

    /// <summary>PWK01 for a completed DTR form: 03, report justifying treatment.</summary>
    public const string DtrFormAttachmentType = "03";

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
                // A retry may follow a write whose request was not raised:
                // the coordinator replays onto the same request, never a second.
                await RaiseAdditionalInformationRequestAsync(backend, existing, request.Decision!);
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
            SubmitterIdentifier = string.IsNullOrWhiteSpace(request.SubmitterIdentifier)
                ? null
                : request.SubmitterIdentifier.Trim(),
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
            // DTR forms the request carried (PWK01 03: report justifying
            // treatment; sent electronically as part of the request).
            ClinicalAttachments = (request.Documentation ?? []).Select(d => new ClinicalAttachment
            {
                AttachmentType = DtrFormAttachmentType,
                TransmissionCode = "EL",
                Description = d.Title ?? "Documentation questionnaire",
                QuestionnaireResponseId = d.QuestionnaireResponseId,
                Questionnaire = d.Questionnaire,
                UploadedDate = d.Authored ?? request.ReceivedAt!.Value,
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
            {
                await RaiseAdditionalInformationRequestAsync(backend, winner, request.Decision!);
                return Ok(winner);
            }
            return Conflict($"Authorization {number} already exists.");
        }

        await RaiseAdditionalInformationRequestAsync(backend, stored, request.Decision!);

        _logger.LogInformation(
            "Automated decision recorded: authorization {AuthNumber} tenant {Tenant} status {Status} by {Actor}",
            LogSanitizer.Sanitize(stored.AuthorizationNumber), LogSanitizer.Sanitize(stored.TenantId), stored.Status,
            LogSanitizer.Sanitize(_actor.UserId));

        return StatusCode(StatusCodes.Status201Created, stored);
    }

    /// <summary>
    /// Raises the additional-information request (PAS-07) a pend names, in
    /// rfai-service, and stamps its handle on the record, through the same
    /// coordinator a reviewer's A4 decision uses. Only for a record this
    /// service owns (the native backend) that is still pended A4: a plan
    /// exclusion or a state rule that decided otherwise asks for nothing.
    ///
    /// A failure leaves the record pended with no request, the coordinator's
    /// recoverable state: the decision stands, the response says nothing was
    /// requested, and a retry of the same request replays onto the same
    /// correlation key.
    /// </summary>
    private async System.Threading.Tasks.Task RaiseAdditionalInformationRequestAsync(
        IAuthorizationBackend backend, Authorization stored, AutomatedDecision decision)
    {
        if (_rfai is null || _repository is null || !backend.IsAuthoritative
            || decision.RequestedInformation is not { Count: > 0 } items
            || (stored.RFAIIssued && !string.IsNullOrEmpty(stored.RFAIReference)))
            return;

        var stamped = await _rfai.EnsureRequestForDecisionAsync(
            stored, items, decision.InformationDueDate, decisionControlNumber: null, HttpContext.RequestAborted);
        if (!stamped) return;

        try
        {
            await _repository.UpdateAsync(stored);
        }
        catch (Exception ex)
        {
            // The request is durable in rfai-service; only the handle is
            // missing, and a retry stamps it again.
            _logger.LogError(ex,
                "Authorization {AuthNumber}: the additional-information request was raised "
                + "but its handle could not be persisted.",
                LogSanitizer.Sanitize(stored.AuthorizationNumber));
        }
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

    /// <summary>
    /// The most recent record this endpoint stored under the submitter's own
    /// identifier for the request (<c>system|value</c> of the PAS Claim's
    /// <c>identifier</c>), in the token's tenant. For fhir-service's PAS
    /// <c>$inquire</c>, which a provider may make with the identifier it
    /// assigned rather than the <c>preAuthRef</c> CHO issued. fhir-service
    /// still checks the inquiry's corroborating key against the record.
    /// </summary>
    [HttpGet("by-submitter-identifier")]
    [ProducesResponseType(typeof(Authorization), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Authorization>> GetBySubmitterIdentifier([FromQuery] string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 300)
            return BadRequest("A submitter identifier of at most 300 characters is required.");

        var stored = await _backends.Resolve()
            .GetBySubmitterIdentifierAsync(identifier.Trim(), HttpContext.RequestAborted);
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

        // What a pend asks for: only a pend asks, and every item says what it is.
        if (decision.RequestedInformation is { Count: > 0 } requested)
        {
            if (decision.Outcome != AutomatedDecisionOutcome.Pended)
                Add("Decision.RequestedInformation", "Only a pend asks the provider for information.");
            else if (requested.Any(i => string.IsNullOrWhiteSpace(i.Description)))
                Add("Decision.RequestedInformation", "Every requested item needs a description.");
            else if (requested.Any(i => i.ServiceLineNumber is < 1))
                Add("Decision.RequestedInformation", "A requested item's service line number is 1 or more.");
            else if (requested.Any(i => i.Questionnaire is { Length: > 500 }
                         || i.Description.Length > 500 || i.LoincCode is { Length: > 20 }
                         || i.ContentModifier is { Length: > 20 } || i.Code is { Length: > 2 }))
                Add("Decision.RequestedInformation", "A requested item's field is too long.");

            if (decision.InformationDueDate is { } due && due < decided)
                Add("Decision.InformationDueDate", "InformationDueDate is before DecidedAt.");
        }
        else if (decision.InformationDueDate.HasValue)
        {
            Add("Decision.InformationDueDate", "A due date needs something requested.");
        }

        // A modification names the requested lines it replaces, each once.
        if (decision.Alternatives is { Count: > 0 } alternatives)
        {
            var lines = request.RequestedServices.Select((s, i) => s.LineNumber ?? i + 1).ToHashSet();
            if (decision.Outcome != AutomatedDecisionOutcome.Modified)
                Add("Decision.Alternatives", "Only a modification certifies an alternative.");
            else if (alternatives.Any(a => !lines.Contains(a.ForLineNumber)))
                Add("Decision.Alternatives", "An alternative replaces a requested line.");
            else if (alternatives.Select(a => a.ForLineNumber).Distinct().Count() != alternatives.Count)
                Add("Decision.Alternatives", "A requested line is replaced by at most one alternative.");
        }
        else if (decision.Outcome == AutomatedDecisionOutcome.Modified)
        {
            Add("Decision.Alternatives", "A modification names the alternative it certifies.");
        }

        var hasApprovedPeriod = decision.ApprovedServiceDateFrom.HasValue || decision.ApprovedServiceDateTo.HasValue;
        switch (decision.Outcome)
        {
            case AutomatedDecisionOutcome.Approved:
            case AutomatedDecisionOutcome.Modified:
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
