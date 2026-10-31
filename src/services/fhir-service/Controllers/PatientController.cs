using FhirService.Services.Identity;
using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Patient resource — read and search.
/// Supports search parameters: _id, name, family, given, birthdate, identifier, gender, _lastUpdated.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: "members:read")]
public class PatientController : FhirControllerBase
{
    private readonly IFhirDataAdapter _adapter;
    private readonly FhirBundleBuilder _bundleBuilder;

    public PatientController(IFhirDataAdapter adapter, FhirBundleBuilder bundleBuilder)
    {
        _adapter = adapter;
        _bundleBuilder = bundleBuilder;
    }

    /// <summary>GET /fhir/r4/Patient/{id} — read a single Patient</summary>
    [HttpGet("Patient/{id}")]
    [ProducesResponseType(typeof(Patient), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 404)]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        var patient = await _adapter.GetPatientAsync(id, TenantId, ct);
        // The resource itself, not just the requested id, must be the bound member.
        return patient is null || IsOutsidePatientContext($"Patient/{patient.Id}")
            ? FhirNotFound("Patient", id)
            : Ok(patient);
    }

    /// <summary>GET /fhir/r4/Patient — search Patients</summary>
    [HttpGet("Patient")]
    [ProducesResponseType(typeof(Bundle), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    public async Task<IActionResult> Search([FromQuery] PatientSearchParams search, CancellationToken ct)
    {
        search.Count = ClampPageSize(search.Count);
        search.Page = ClampPage(search.Page);

        if (HttpContext.Items[FhirService.Middleware.SmartScopeEnforcementMiddleware.PayerToPayerIdentifierLookupItemKey] is true)
            return await PayerToPayerIdentifierLookupAsync(search, ct);

        // A patient-bound token searches its own record only, whatever else
        // the query says (name, birthdate, identifier...).
        if (AuthorizedMemberId is { } bound)
        {
            if (!string.IsNullOrEmpty(search.Id) && !string.Equals(search.Id, bound, StringComparison.Ordinal))
            {
                return Ok(_bundleBuilder.Build(
                    Array.Empty<Patient>(), 0, search.Page, search.Count,
                    "Patient", FhirBaseUrl, RawQueryString));
            }
            search.Id = bound;
        }

        var (items, total) = await _adapter.SearchPatientsAsync(search, TenantId, ct);
        if (ResultsLeaveConfinement("Patient", items.Select(p => $"Patient/{p.Id}")))
            return FhirBadGateway("Patient search could not be confined to the authorized member.");

        var bundle = _bundleBuilder.Build(
            items, total, search.Page, search.Count,
            "Patient", FhirBaseUrl, RawQueryString);

        return Ok(bundle);
    }

    /// <summary>
    /// PDex requirement 35: a requesting payer turns the <c>MemberIdentifier</c>
    /// <c>$member-match</c> returned into the member's logical Patient id. The
    /// middleware admitted only <c>Patient?identifier=</c> under
    /// <c>system/PayerToPayer.read</c>; here the caller must be registered to a
    /// payer, and a Patient is returned only when the consent naming that payer
    /// (recorded by member match, ADR 022) is in force for the member. Anyone
    /// else is left out of the searchset, so "no such member" and "no consent"
    /// look the same, as member match's own 422 does.
    /// </summary>
    private async Task<IActionResult> PayerToPayerIdentifierLookupAsync(
        PatientSearchParams search, CancellationToken ct)
    {
        var logger = HttpContext.RequestServices
            .GetRequiredService<ILogger<PatientController>>();
        if (!TryBindReceivingPayer(null, logger, "patient identifier lookup", out var payerId))
            return FhirForbidden(PayerToPayerController.PayerBindingMessage);

        var consents = HttpContext.RequestServices
            .GetRequiredService<FhirService.Services.PayerToPayer.IPayerToPayerConsentAttestationStore>();
        var now = (HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().UtcDateTime;

        var (items, _) = await _adapter.SearchPatientsAsync(search, TenantId, ct);
        var consented = new List<Patient>();
        foreach (var patient in items)
        {
            if (!string.IsNullOrEmpty(patient.Id)
                && await consents.FindInForceAsync(TenantId, patient.Id, payerId, now, ct) is not null)
            {
                consented.Add(patient);
            }
        }

        return Ok(_bundleBuilder.Build(
            consented, consented.Count, search.Page, search.Count,
            "Patient", FhirBaseUrl, RawQueryString));
    }
}
