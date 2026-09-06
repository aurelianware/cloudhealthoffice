using FhirService.Services.Cdex;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Da Vinci CDex — the response half of the additional-information round trip on
/// a pended prior authorization.
///
/// <c>POST fhir/r4/$submit-attachment</c> is the CDex operation a provider
/// invokes to send the documentation the payer asked for. The REQUEST half — the
/// Task on the CDex Task Attachment Request profile that says WHAT is needed —
/// is served by <see cref="TaskController"/>, because it is a Task read/search
/// like any other and does not deserve a private route.
///
/// AUTHORIZATION. Authenticated, and gated on a WRITE scope by
/// <c>SmartScopeEnforcementMiddleware</c> — a read scope is not enough to put
/// documents into a payer's record. Tenant comes from the authenticated context;
/// nothing in the Parameters payload selects one. The submission is bound to its
/// request by the tracking id, the authorization named in <c>AttachTo</c>, and
/// the provider the request was addressed to.
///
/// This is deliberately NOT routed through the Provider Access consent gate.
/// That gate governs a provider READING a member's clinical record; this is a
/// provider answering the payer's question about the provider's own
/// prior-authorization request — a payer/provider transaction, governed by the
/// PAS/CDex authorization model. The separation introduced for Provider Access
/// is preserved rather than borrowed from.
/// </summary>
[Route("fhir/r4")]
[Authorize]
public sealed class CdexController : FhirControllerBase
{
    private readonly ICdexAttachmentSubmissionService _submissions;
    private readonly ILogger<CdexController> _logger;

    public CdexController(
        ICdexAttachmentSubmissionService submissions,
        ILogger<CdexController> logger)
    {
        _submissions = submissions;
        _logger = logger;
    }

    /// <summary>
    /// CDex <c>$submit-attachment</c> — submit the documentation a pended prior
    /// authorization is waiting on.
    ///
    /// IDEMPOTENT. A submission's identity is derived from the tenant, the
    /// request, the tracking id and the content itself, so replaying the same
    /// call records nothing a second time, transitions nothing a second time, and
    /// does not restart the review. A materially DIFFERENT document under the
    /// same request is an additional response, appended alongside the first
    /// rather than replacing it.
    ///
    /// ACCEPTING DOCUMENTS IS NOT APPROVING. At most this returns the
    /// authorization to review; the decision stays with a reviewer.
    /// </summary>
    [HttpPost(CdexCanonicalUrls.SubmitAttachmentRoute)]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    [ProducesResponseType(typeof(OperationOutcome), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    [ProducesResponseType(typeof(OperationOutcome), 409)]
    [ProducesResponseType(typeof(OperationOutcome), 422)]
    public async Task<IActionResult> SubmitAttachment([FromBody] Parameters? parameters)
    {
        if (parameters is null)
            return FhirBadRequest("A Parameters resource is required.");

        var result = await _submissions.SubmitAsync(
            parameters, TenantId, CallerId(), HttpContext.RequestAborted);

        Audit(result);

        return result.Disclosure switch
        {
            CdexSubmissionDisclosure.Success => Ok(BuildAcceptedOutcome(result)),

            CdexSubmissionDisclosure.BadRequest =>
                FhirBadRequest(result.Detail ?? "The submission is incomplete."),

            CdexSubmissionDisclosure.UnprocessableContent =>
                FhirUnprocessable(result.Detail ?? "The submitted content was not accepted."),

            // ONE answer for unknown, wrong tenant, wrong authorization and wrong
            // provider. Distinguishing them would turn a tracking id into a probe
            // for which authorizations exist and who they belong to. The real
            // category is kept in the PHI-free audit line above.
            CdexSubmissionDisclosure.Unavailable => StatusCode(404, Outcome(
                OperationOutcome.IssueType.NotFound,
                "No open additional-information request matching the supplied identifiers "
                + "is available.")),

            CdexSubmissionDisclosure.Conflict => StatusCode(409, Outcome(
                OperationOutcome.IssueType.BusinessRule,
                result.Outcome == CdexSubmissionOutcome.RequestAtCapacity
                    ? "This additional-information request has reached its attachment limit."
                    : "This additional-information request is no longer open for a response.")),

            _ => StatusCode(503, Outcome(
                OperationOutcome.IssueType.Transient,
                "The submission could not be completed. Retrying is safe — a repeated "
                + "submission of the same content is recorded once.")),
        };
    }

    /// <summary>
    /// The success answer. It reports what happened — accepted or already had it,
    /// and whether the authorization went back to review — without echoing any
    /// part of the submitted content.
    /// </summary>
    private static OperationOutcome BuildAcceptedOutcome(CdexSubmissionResult result)
    {
        var replay = result.Outcome == CdexSubmissionOutcome.DuplicateReplay;

        var text = replay
            ? "This submission was already recorded against the request; nothing was changed."
            : $"{result.Recorded} document(s) recorded against the request."
              + (result.ResumedReview
                  ? " The prior authorization has returned to review."
                  : " The prior authorization remains under review.");

        return new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Information,
                    Code = OperationOutcome.IssueType.Informational,
                    Diagnostics = text,
                }
            ]
        };
    }

    private static OperationOutcome Outcome(OperationOutcome.IssueType code, string diagnostics)
        => new()
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = code,
                    Diagnostics = diagnostics,
                }
            ]
        };

    /// <summary>
    /// Records the submission with SAFE identifiers only: tenant, caller, the
    /// tracking id quoted, the request and authorization it correlated to, the
    /// outcome category and how many artifacts were recorded.
    ///
    /// Never the content, a filename, a document title, a diagnosis, a member
    /// demographic, the Parameters payload, or a token.
    /// </summary>
    private void Audit(CdexSubmissionResult result)
    {
        var caller = CallerId();

        if (result.Succeeded)
        {
            _logger.LogInformation(
                "CDex $submit-attachment: tenant={Tenant} caller={Caller} tracking={Tracking} "
                + "request={Request} authorization={Auth} outcome={Outcome} recorded={Recorded} "
                + "resumedReview={Resumed} at={At}",
                SanitizeForLog(TenantId), SanitizeForLog(caller),
                SanitizeForLog(result.TrackingId), SanitizeForLog(result.RequestId),
                SanitizeForLog(result.AuthorizationNumber), result.Outcome,
                result.Recorded, result.ResumedReview, DateTime.UtcNow);
            return;
        }

        _logger.LogWarning(
            "CDex $submit-attachment refused: tenant={Tenant} caller={Caller} tracking={Tracking} "
            + "request={Request} outcome={Outcome} at={At}",
            SanitizeForLog(TenantId), SanitizeForLog(caller),
            SanitizeForLog(result.TrackingId), SanitizeForLog(result.RequestId),
            result.Outcome, DateTime.UtcNow);
    }

    /// <summary>
    /// The authenticated caller, from the validated token — the same resolution
    /// <c>Claim/$inquire</c> uses. Never read from a header or the body.
    /// </summary>
    private string? CallerId()
    {
        foreach (var claimType in new[]
                 {
                     "sub",
                     System.Security.Claims.ClaimTypes.NameIdentifier,
                     "client_id",
                     "azp",
                 })
        {
            var value = User?.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }
}
