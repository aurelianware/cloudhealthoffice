using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer;
using Microsoft.AspNetCore.Mvc;

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
public sealed class PayerToPayerMemberMatchController : FhirControllerBase
{
    private readonly IPayerToPayerMemberMatchService _matcher;

    public PayerToPayerMemberMatchController(IPayerToPayerMemberMatchService matcher) => _matcher = matcher;

    [HttpPost("Patient/$member-match")]
    public async Task<IActionResult> MemberMatch(
        [FromBody] MemberMatchRequestDto? body, CancellationToken ct)
    {
        if (body is null)
            return FhirBadRequest("A member-match request body is required.");

        // The receiving payer must identify itself so the match is auditable
        // ("which receiving payer asked"); normalize it so audit/log values are stable.
        var receivingPayerId = body.ReceivingPayerId?.Trim();
        if (string.IsNullOrEmpty(receivingPayerId))
            return FhirBadRequest("receivingPayerId is required to identify the requesting payer for audit.");

        var request = new MemberMatchRequest
        {
            TenantId = TenantId,                 // from the authenticated context, not the body
            ReceivingPayerId = receivingPayerId,
            InitiatedBy = SmartPatientId is null ? receivingPayerId : $"patient:{SmartPatientId}",
            FamilyName = body.FamilyName,
            GivenName = body.GivenName,
            BirthDate = body.BirthDate,
            Gender = body.Gender,
            MemberId = body.MemberId,
            Ssn = body.Ssn,
            PostalCode = body.PostalCode,
            Phone = body.Phone,
            Email = body.Email,
            RequestedPayerId = body.RequestedPayerId,
            RequestedSubscriberId = body.RequestedSubscriberId,
            AsOfDate = body.AsOfDate,
        };

        var result = await _matcher.MatchAsync(request, ct);
        return result.Outcome switch
        {
            MemberMatchOutcome.Matched =>
                Ok(PayerToPayerMemberMatchResponseBuilder.Build(result.Member!, result.Coverage)),

            MemberMatchOutcome.InsufficientCriteria => FhirBadRequest(
                "Insufficient identifying criteria: supply a member/subscriber id or SSN, or a family name with a birth date."),

            // No-match, ambiguous identity, ambiguous coverage, and cross-tenant all
            // collapse to one generic 422 so the endpoint cannot reveal whether a
            // given identity exists, how many candidates matched, or that another
            // tenant holds the member.
            _ => FhirUnprocessable(
                "The supplied identifiers did not resolve to a single member and coverage; no data returned."),
        };
    }
}

/// <summary>
/// Request body for <c>Patient/$member-match</c>. It carries the member's
/// identity attributes and the receiving-payer context; the tenant and consent
/// are decided server-side, never accepted from the request.
/// </summary>
public sealed class MemberMatchRequestDto
{
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
