using FhirService.Services.Identity;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Inbound Payer-to-Payer respond (CMS-0057-F P2P-01). A receiving payer POSTs
/// the transitioning member's identifiers and opt-in authorization; Cloud Health
/// Office — the prior payer — returns a member-scoped FHIR export package from
/// its own authoritative data.
///
/// This controller is a THIN routing surface. All matching, authorization, and
/// export logic lives in <see cref="IPayerToPayerExchangeService"/>. Routes sit
/// under <c>/fhir/r4</c> so SmartScopeEnforcementMiddleware applies the same
/// JWT/SMART enforcement as the rest of the FHIR surface. The tenant is taken
/// from the authenticated request context, never from the body, so a caller
/// cannot request another tenant's data.
///
/// This is the P2P-01 "respond" path only. Outbound initiation (P2P-02, see
/// <see cref="PayerToPayerOutboundController"/>) and the FHIR
/// <c>$member-match</c> / concurrent-coverage operation (P2P-04, see
/// <see cref="PayerToPayerMemberMatchController"/>) are separate surfaces.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: null)]
public sealed class PayerToPayerController : FhirControllerBase
{
    private readonly IPayerToPayerExchangeService _exchange;

    public PayerToPayerController(IPayerToPayerExchangeService exchange) => _exchange = exchange;

    [HttpPost("PayerToPayer/$member-data-export")]
    public async Task<IActionResult> Export(
        [FromBody] PayerToPayerExportRequestDto? body, CancellationToken ct)
    {
        // D-13: a payer-to-payer export is a backend-to-backend operation. The
        // middleware already admits system-context tokens only; this repeats
        // the check here, BEFORE the body is looked at, so a patient-bound or
        // user-context token is refused identically whatever member it names.
        if (!IsBackendSystemCaller)
            return FhirForbidden(BackendOnlyMessage);

        if (body is null)
            return FhirBadRequest("A Payer-to-Payer export request body is required.");

        // The receiving payer must identify itself so the exchange is auditable
        // ("which receiving payer"); normalize it so audit/log values are stable.
        //
        // NOT YET BOUND to the authenticated client: CHO has no client -> payer
        // registration today, so this value is still self-declared by the
        // caller. What the caller cannot do any more is be a member or a
        // provider app, and the audit's InitiatedBy is now the authenticated
        // client, not this body value. (D-13 remainder; see the gap matrix.)
        var receivingPayerId = body.ReceivingPayerId?.Trim();
        if (string.IsNullOrEmpty(receivingPayerId))
            return FhirBadRequest("receivingPayerId is required to identify the requesting payer for audit.");

        var request = new PayerToPayerExchangeRequest
        {
            TenantId = TenantId,                 // from the authenticated context, not the body
            ReceivingPayerId = receivingPayerId,
            InitiatedBy = $"client:{AuthenticatedActorId ?? "unknown"}",
            MemberId = body.MemberId,
            LastName = body.LastName,
            Dob = body.Dob,
            Gender = body.Gender,
        };

        var result = await _exchange.RespondAsync(request, ct);
        return result.Outcome switch
        {
            PayerToPayerOutcome.Exported => Ok(result.Bundle),
            PayerToPayerOutcome.NotAuthorized => Forbidden(
                "Member has not authorized a Payer-to-Payer exchange (no active opt-in consent)."),
            PayerToPayerOutcome.AmbiguousMatch => FhirUnprocessable(
                "The supplied identifiers matched more than one member; refusing to return data."),
            PayerToPayerOutcome.InsufficientCriteria => FhirBadRequest(
                "A member identifier is required to resolve the member for export."),
            // NoMatch and TenantMismatch both surface as 404 so cross-tenant
            // existence is never revealed.
            _ => FhirNotFound("Member", SanitizeForLog(body.MemberId) ?? "unknown"),
        };
    }

    internal const string BackendOnlyMessage =
        "Payer-to-Payer operations are restricted to payer backend (system) clients.";

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
/// Request body for <c>PayerToPayer/$member-data-export</c>. It carries only the
/// member identifiers and receiving-payer context — the member's opt-in is
/// decided server-side, never accepted from the request.
/// </summary>
public sealed class PayerToPayerExportRequestDto
{
    public string? ReceivingPayerId { get; set; }
    public string? MemberId { get; set; }
    public string? LastName { get; set; }
    public string? Dob { get; set; }
    public string? Gender { get; set; }
}
