using FhirService.Services.Identity;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

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
    private readonly ILogger<PayerToPayerController> _logger;

    public PayerToPayerController(IPayerToPayerExchangeService exchange, ILogger<PayerToPayerController> logger)
    {
        _exchange = exchange;
        _logger = logger;
    }

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

        // D-13: the receiving payer is the payer the authenticated client is
        // REGISTERED to (payer_id from CHO's registration authority), never
        // one the body names. No mapping, or a body naming another payer, is
        // one fixed refusal given before any member is looked at.
        if (!TryBindReceivingPayer(body?.ReceivingPayerId, _logger, "export", out var receivingPayerId))
            return FhirForbidden(PayerBindingMessage);

        if (body is null)
            return FhirBadRequest("A Payer-to-Payer export request body is required.");

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
            PayerToPayerOutcome.NotAuthorized => FhirForbidden(
                "Member has not authorized a Payer-to-Payer exchange (no active opt-in consent)."),
            PayerToPayerOutcome.AmbiguousMatch => FhirUnprocessable(
                "The supplied identifiers matched more than one member; refusing to return data."),
            PayerToPayerOutcome.InsufficientCriteria => FhirBadRequest(
                "A member identifier is required to resolve the member for export."),
            // NoMatch and TenantMismatch both surface as 404 so cross-tenant
            // existence is never revealed.
            _ => FhirNotFound("Member", StripLineBreaks(body.MemberId) ?? "unknown"),
        };
    }

    internal const string BackendOnlyMessage =
        "Payer-to-Payer operations are restricted to payer backend (system) clients.";

    internal const string PayerBindingMessage =
        "Payer-to-Payer operations require a backend client registered with Cloud Health Office to the receiving payer.";

    /// <summary>
    /// Removes CR and LF from a value that is stored or returned (not only
    /// logged). For log lines use <see cref="LogSanitizer.Sanitize(string?)"/>.
    /// </summary>
    private static string StripLineBreaks(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
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
