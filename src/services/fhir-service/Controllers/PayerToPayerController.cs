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
/// This is the P2P-01 "respond" path only. Outbound initiation (P2P-02) and the
/// FHIR <c>$member-match</c> / concurrent-coverage operation (P2P-04) are not
/// implemented here.
/// </summary>
[Route("fhir/r4")]
public sealed class PayerToPayerController : FhirControllerBase
{
    private readonly IPayerToPayerExchangeService _exchange;

    public PayerToPayerController(IPayerToPayerExchangeService exchange) => _exchange = exchange;

    [HttpPost("PayerToPayer/$member-data-export")]
    public async Task<IActionResult> Export(
        [FromBody] PayerToPayerExportRequestDto? body, CancellationToken ct)
    {
        if (body is null)
            return FhirBadRequest("A Payer-to-Payer export request body is required.");

        var request = new PayerToPayerExchangeRequest
        {
            TenantId = TenantId,                 // from the authenticated context, not the body
            ReceivingPayerId = body.ReceivingPayerId ?? string.Empty,
            InitiatedBy = SmartPatientId is null ? body.ReceivingPayerId : $"patient:{SmartPatientId}",
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
