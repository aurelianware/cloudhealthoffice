using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Outbound;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Outbound Payer-to-Payer initiation (CMS-0057-F P2P-02). On an authorized
/// coverage transition, Cloud Health Office — the member's new/current payer —
/// initiates the exchange against the member's prior payer, resolves the member
/// with that payer, and requests the member-scoped data package.
///
/// This controller is a THIN routing surface. All orchestration — endpoint
/// resolution, authorization, remote member-match, export, validation, audit,
/// and exchange state — lives in <see cref="IPayerToPayerOutboundService"/>.
///
/// Targeting is by payer id ONLY. The remote location is resolved from the
/// trusted payer directory, so a caller can never make Cloud Health Office call
/// an arbitrary URL (SSRF). The tenant comes from the authenticated request
/// context, never from the body, and a patient-scoped token may only initiate
/// for the patient it is bound to. The member's opt-in is decided server-side —
/// it is not a field on this request.
///
/// Routes sit under <c>/fhir/r4</c> so SmartScopeEnforcementMiddleware applies
/// the same JWT/SMART enforcement as the rest of the FHIR surface.
/// </summary>
[Route("fhir/r4")]
public sealed class PayerToPayerOutboundController : FhirControllerBase
{
    private readonly IPayerToPayerOutboundService _outbound;

    public PayerToPayerOutboundController(IPayerToPayerOutboundService outbound) => _outbound = outbound;

    [HttpPost("PayerToPayer/$initiate")]
    public async Task<IActionResult> Initiate(
        [FromBody] PayerToPayerInitiateRequestDto? body, CancellationToken ct)
    {
        if (body is null)
            return FhirBadRequest("A Payer-to-Payer initiation request body is required.");

        var memberId = body.MemberId?.Trim();
        if (string.IsNullOrEmpty(memberId))
            return FhirBadRequest("memberId is required to identify the member the exchange is for.");

        var targetPayerId = body.TargetPayerId?.Trim();
        if (string.IsNullOrEmpty(targetPayerId))
            return FhirBadRequest(
                "targetPayerId is required. It names a payer in the configured Payer-to-Payer directory; "
                + "endpoint locations are never accepted from a request.");

        // A patient-scoped token is bound to its own member: it may initiate an
        // exchange for that member and no one else.
        if (SmartPatientId is { } boundPatient && !string.Equals(boundPatient, memberId, StringComparison.Ordinal))
            return Forbidden("A patient-scoped token may only initiate a Payer-to-Payer exchange for its own member.");

        var result = await _outbound.InitiateAsync(new PayerToPayerOutboundRequest
        {
            TenantId = TenantId,                 // from the authenticated context, not the body
            MemberId = memberId,
            TargetPayerId = targetPayerId,
            TransitionKey = body.TransitionKey,
            AsOfDate = body.AsOfDate,
            InitiatedBy = SmartPatientId is null ? "system" : $"patient:{SmartPatientId}",
        }, ct);

        return result.Exchange.Status switch
        {
            PayerToPayerOutboundStatus.Completed => Ok(Receipt(result)),

            PayerToPayerOutboundStatus.NotAuthorized => Forbidden(
                "Member has not authorized a Payer-to-Payer exchange (no active opt-in consent)."),

            PayerToPayerOutboundStatus.NoMatch or PayerToPayerOutboundStatus.Ambiguous => FhirUnprocessable(
                "The prior payer did not resolve the member to a single identity; no data was requested."),

            _ => FailureResponse(result.Exchange.Failure),
        };
    }

    private IActionResult FailureResponse(PayerToPayerOutboundFailure failure) => failure switch
    {
        PayerToPayerOutboundFailure.TargetPayerNotConfigured => FhirUnprocessable(
            "No Payer-to-Payer endpoint is configured for the requested payer in this tenant."),

        PayerToPayerOutboundFailure.LocalCoverageAmbiguous => FhirUnprocessable(
            "The member holds more than one coverage with the requested payer; the exchange context is ambiguous."),

        // Remote-side problems are reported as a gateway failure with an
        // operator-facing category only — the peer's own response body is never
        // passed through.
        PayerToPayerOutboundFailure.RemoteUnauthorized => FhirBadGateway(
            "The prior payer rejected the exchange authorization."),
        PayerToPayerOutboundFailure.RemoteUnavailable => FhirBadGateway(
            "The prior payer's Payer-to-Payer endpoint is unavailable."),
        PayerToPayerOutboundFailure.InvalidRemoteResponse => FhirBadGateway(
            "The prior payer returned a response that could not be validated for this member."),

        // MemberNotFound and TenantMismatch both surface as 404 so cross-tenant
        // existence is never revealed.
        _ => FhirNotFound("Member", "unknown"),
    };

    /// <summary>
    /// The exchange receipt: what was initiated, how it ended, and — when a
    /// package was received — the validated Bundle, carrying the Provenance
    /// stamp that names the payer it came from.
    /// </summary>
    private static Parameters Receipt(PayerToPayerOutboundResult result)
    {
        var parameters = new Parameters();
        parameters.Add("exchangeId", new FhirString(result.Exchange.ExchangeId));
        parameters.Add("status", new FhirString(result.Exchange.Status.ToString()));
        parameters.Add("targetPayerId", new FhirString(result.Exchange.TargetPayerId));
        parameters.Add("resourceCount", new Integer(result.Exchange.ReceivedResourceCount));
        parameters.Add("replay", new FhirBoolean(result.IsReplay));

        if (result.Package is not null)
            parameters.Parameter.Add(new Parameters.ParameterComponent
            {
                Name = "package",
                Resource = result.Package.Bundle,
            });

        return parameters;
    }

    private IActionResult Forbidden(string diagnostics)
        => StatusCode(403, new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Forbidden,
                    Diagnostics = diagnostics,
                },
            ],
        });
}

/// <summary>
/// Request body for <c>PayerToPayer/$initiate</c>. It carries references only —
/// which member, which configured payer, which coverage transition. There is
/// deliberately no endpoint/URL field (SSRF), no consent field (decided
/// server-side), and no tenant field (taken from the authenticated context).
/// </summary>
public sealed class PayerToPayerInitiateRequestDto
{
    /// <summary>The CHO member the exchange is for.</summary>
    public string? MemberId { get; set; }

    /// <summary>Id of a payer in the tenant's configured Payer-to-Payer directory.</summary>
    public string? TargetPayerId { get; set; }

    /// <summary>Coverage-transition key; makes a retried initiation idempotent.</summary>
    public string? TransitionKey { get; set; }

    /// <summary>Date the prior coverage context is requested "as of" (yyyy-MM-dd).</summary>
    public string? AsOfDate { get; set; }
}
