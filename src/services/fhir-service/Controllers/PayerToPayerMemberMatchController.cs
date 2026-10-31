using FhirService.Services.Identity;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.BulkMemberMatch;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace FhirService.Controllers;

/// <summary>
/// Payer-to-Payer member match (CMS-0057-F P2P-04). A receiving payer POSTs the
/// transitioning member's identity attributes; Cloud Health Office resolves the
/// same person within the tenant and returns the stable member/coverage context.
///
/// This controller is a THIN routing surface. All matching, coverage selection,
/// and normalization live in <see cref="IPayerToPayerMemberMatchService"/>.
/// Routes sit under <c>/fhir/r4</c> so SmartScopeEnforcementMiddleware applies
/// the same JWT/SMART enforcement as the rest of the FHIR surface. The tenant is
/// taken from the authenticated request context, never from the body, so a
/// caller cannot match against another tenant's members.
///
/// This is identity resolution only. It does NOT return claims/EOB data and does
/// NOT gate on consent — the P2P-01 respond path
/// (<c>PayerToPayer/$member-data-export</c>) enforces the member's opt-in when
/// data is actually pulled, so P2P-03 stays independent.
///
/// Failure responses are deliberately generic (a single 422 for no-match /
/// ambiguous / cross-tenant) so the endpoint cannot be used to enumerate members
/// or probe which identities exist.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: null)]
public sealed class PayerToPayerMemberMatchController : FhirControllerBase
{
    private readonly IPayerToPayerMemberMatchService _matcher;
    private readonly ILogger<PayerToPayerMemberMatchController> _logger;

    public PayerToPayerMemberMatchController(
        IPayerToPayerMemberMatchService matcher, ILogger<PayerToPayerMemberMatchController> logger)
    {
        _matcher = matcher;
        _logger = logger;
    }

    /// <summary>
    /// POST /fhir/r4/Patient/$member-match. Two request forms, told apart by
    /// the body: a FHIR <c>Parameters</c> is the HRex/PDex form every other
    /// payer sends (<c>MemberPatient</c>, <c>CoverageToMatch</c>, optional
    /// <c>CoverageToLink</c>, <c>Consent</c>) and is answered with the HRex
    /// <c>MemberIdentifier</c> Parameters; anything else is CHO's own JSON form
    /// (<see cref="MemberMatchRequestDto"/>), answered with a Bundle as before.
    /// Both run the same matcher, consent check and refusals.
    /// </summary>
    [HttpPost("Patient/$member-match")]
    public async Task<IActionResult> MemberMatch([FromBody] JsonObject? body, CancellationToken ct)
    {
        // D-13: member-match is payer-to-payer (backend) only. Refused before
        // the body is read, so the answer does not depend on the member named.
        if (!IsBackendSystemCaller)
            return FhirForbidden(PayerToPayerController.BackendOnlyMessage);

        var hrex = body is not null && body["resourceType"] is JsonValue type
                   && type.TryGetValue<string>(out var resourceType) && resourceType == "Parameters";

        MemberMatchRequestDto? dto = null;
        BulkMemberMatchEntry? entry = null;
        if (!hrex && body is not null)
        {
            try
            {
                dto = body.Deserialize<MemberMatchRequestDto>(DtoJson);
            }
            catch (JsonException)
            {
                return FhirBadRequest("The member-match request body could not be read.");
            }
        }

        // D-13: HRex/PDex $member-match carries no receiving-payer parameter;
        // the receiving payer is the one the authenticated client is
        // registered to. A client with no registered payer, or a body naming
        // another payer, gets one fixed refusal before any member is looked at.
        if (!TryBindReceivingPayer(dto?.ReceivingPayerId, _logger, "member-match", out var receivingPayerId))
            return FhirForbidden(PayerToPayerController.PayerBindingMessage);

        if (body is null)
            return FhirBadRequest("A member-match request body is required.");

        if (hrex && !BulkMemberMatchRequestReader.TryReadSingle(body, out entry, out var shapeError))
            return FhirBadRequest(shapeError!);

        var request = entry is not null
            ? new MemberMatchRequest
            {
                TenantId = TenantId,
                ReceivingPayerId = receivingPayerId,
                InitiatedBy = $"client:{AuthenticatedActorId ?? "unknown"}",
                FamilyName = entry.FamilyName,
                GivenName = entry.GivenName,
                BirthDate = entry.BirthDate,
                Gender = entry.Gender,
                MemberId = entry.MemberId,
                PostalCode = entry.PostalCode,
                Phone = entry.Phone,
                Email = entry.Email,
                AsOfDate = entry.AsOfDate,
                Consent = entry.Consent,
            }
            : new MemberMatchRequest
            {
                TenantId = TenantId,                 // from the authenticated context, not the body
                ReceivingPayerId = receivingPayerId,
                InitiatedBy = $"client:{AuthenticatedActorId ?? "unknown"}",
                FamilyName = dto!.FamilyName,
                GivenName = dto.GivenName,
                BirthDate = dto.BirthDate,
                Gender = dto.Gender,
                MemberId = dto.MemberId,
                Ssn = dto.Ssn,
                PostalCode = dto.PostalCode,
                Phone = dto.Phone,
                Email = dto.Email,
                RequestedPayerId = dto.RequestedPayerId,
                RequestedSubscriberId = dto.RequestedSubscriberId,
                AsOfDate = dto.AsOfDate,
                Consent = dto.Consent,
            };

        var result = await _matcher.MatchAsync(request, ct);

        return result.Outcome switch
        {
            MemberMatchOutcome.Matched when hrex => new ContentResult
            {
                Content = PayerToPayerMemberMatchResponseBuilder.BuildParameters(result.Member!).ToJsonString(),
                ContentType = "application/fhir+json",
                StatusCode = StatusCodes.Status200OK,
            },
            MemberMatchOutcome.Matched =>
                Ok(PayerToPayerMemberMatchResponseBuilder.Build(result.Member!, result.Coverage)),
            // Decided before any member is looked at, so it reveals nothing
            // about membership (PDex answers an unusable consent with 422).
            MemberMatchOutcome.ConsentNotAccepted => FhirUnprocessable(ConsentNotAcceptedMessage),
            MemberMatchOutcome.InsufficientCriteria => FhirBadRequest(
                "Insufficient identifying criteria: supply a member or subscriber id, or a family name with a birth date."),
            // No-match, ambiguous identity, ambiguous coverage, and cross-tenant all
            // collapse to one generic 422 so the endpoint cannot reveal whether a
            // given identity exists, how many candidates matched, or that another
            // tenant holds the member. HRex asks for 422 on no match and on more
            // than one.
            _ => FhirUnprocessable(
                "The supplied identifiers did not resolve to a single member and coverage; no data returned."),
        };
    }

    /// <summary>How CHO's own JSON form is read: the web defaults MVC binds request bodies with.</summary>
    private static readonly JsonSerializerOptions DtoJson = new(JsonSerializerDefaults.Web);

    internal const string ConsentNotAcceptedMessage =
        "A Payer-to-Payer member-match requires the member's consent: an active HRex Consent (scope patient-privacy, "
        + "category IDSCL, policy #sensitive, provision permit in force) naming the requesting payer as information "
        + "recipient and no party other than that payer and this plan. Data segmentation for the #regular policy is "
        + "not supported.";
}

/// <summary>
/// Request body for <c>Patient/$member-match</c>. It carries the member's
/// identity attributes and the receiving-payer context; the tenant and consent
/// are decided server-side, never accepted from the request.
/// </summary>
public sealed class MemberMatchRequestDto
{
    /// <summary>
    /// The member's HRex consent (a FHIR Consent resource) naming the requesting
    /// payer as information recipient. Required (D-13, ADR 022).
    /// </summary>
    public System.Text.Json.Nodes.JsonObject? Consent { get; set; }

    public string? ReceivingPayerId { get; set; }

    public string? FamilyName { get; set; }
    public string? GivenName { get; set; }
    public string? BirthDate { get; set; }
    public string? Gender { get; set; }
    public string? MemberId { get; set; }
    public string? Ssn { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    public string? RequestedPayerId { get; set; }
    public string? RequestedSubscriberId { get; set; }
    public string? AsOfDate { get; set; }
}
