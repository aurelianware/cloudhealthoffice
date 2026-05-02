using System.Text;
using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 ExplanationOfBenefit resource — read and search (capability
/// 5.11). Thin proxy over claims-service's
/// <c>FhirExplanationOfBenefitController</c>; claims-service owns the
/// canonical CHO projection. Mirrors the BP 5.8
/// <see cref="InsurancePlanController"/> proxy and the Provider 5.7-5.9
/// <see cref="ProviderDirectoryController"/> proxies — fhir-service
/// remains the single FHIR façade for external consumers while each
/// domain service owns its own projection.
///
/// <para>
/// SMART patient binding is enforced upstream by
/// <see cref="Middleware.SmartScopeEnforcementMiddleware"/>: it rejects
/// requests where an explicit <c>patient</c> param does not match the
/// bound patient and stores the bound id in
/// <c>HttpContext.Items["SmartPatientId"]</c> for the controller to
/// auto-inject. CMS-0057-F Patient Access requires EOBs searchable by
/// patient with one of those two paths satisfied.
/// </para>
/// </summary>
[Route("fhir/r4")]
public class ExplanationOfBenefitController : FhirControllerBase
{
    /// <summary>
    /// Back-compat alias used by tests that referenced the
    /// controller-local constant before capability 5.11 introduced the
    /// shared <see cref="UpstreamClientNames.ClaimsService"/> name. New
    /// code should use the shared constant directly.
    /// </summary>
    public const string ClaimsServiceClientName = UpstreamClientNames.ClaimsService;

    private readonly HttpClient _claimsServiceClient;
    private readonly ILogger<ExplanationOfBenefitController> _logger;

    public ExplanationOfBenefitController(
        IHttpClientFactory httpClientFactory,
        ILogger<ExplanationOfBenefitController> logger)
    {
        _claimsServiceClient = httpClientFactory.CreateClient(UpstreamClientNames.ClaimsService);
        _logger = logger;
    }

    /// <summary>
    /// GET /fhir/r4/ExplanationOfBenefit/{id} — read EOB by claim version
    /// id. Proxies to claims-service /fhir/ExplanationOfBenefit/{id}; the
    /// upstream owns the canonical CHO projection and resolves
    /// adjustment chains to their head version (capability 5.11
    /// Decision 11). Path id is URL-encoded so embedded slashes / spaces
    /// don't bypass the upstream route binding.
    /// </summary>
    [HttpGet("ExplanationOfBenefit/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadEob(string id, CancellationToken ct)
        => ProxyClaimsServiceAsync(
            "ExplanationOfBenefit",
            $"fhir/ExplanationOfBenefit/{Uri.EscapeDataString(id)}",
            ct);

    /// <summary>
    /// GET /fhir/r4/ExplanationOfBenefit?patient=&amp;_id=&amp;_count=&amp;_page=
    /// — search EOBs. Forwards the FHIR search query string to
    /// claims-service /fhir/ExplanationOfBenefit unchanged after
    /// auto-injecting the SMART-bound patient id when the caller did
    /// not supply <c>patient</c>. CMS-0057-F requires patient context on
    /// EOB search; if neither <c>patient</c> nor <c>_id</c> nor a SMART
    /// binding is available we short-circuit to a FHIR 400 rather than
    /// forwarding a request that's guaranteed to fail upstream.
    /// </summary>
    [HttpGet("ExplanationOfBenefit")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchEobs(CancellationToken ct = default)
    {
        var query = HttpContext.Request.Query;
        // Normalize FHIR-typed reference form — `patient=Patient/123` is
        // equivalent to `patient=123` per FHIR search semantics, and
        // claims-service stores raw member ids. Without the strip, an
        // upstream search for `Patient/{id}` silently returns empty.
        // SmartPatientId is already normalized by the middleware so it
        // doesn't need the same treatment, but mirror it here for symmetry.
        var explicitPatient = StripPatientPrefix(query["patient"].FirstOrDefault());
        var explicitId = query["_id"].FirstOrDefault();

        // SMART scope enforcement middleware already rejects a mismatched
        // explicit patient param and surfaces the bound patient id via
        // SmartPatientId. Auto-inject when the caller didn't provide one
        // so patient-app callers don't need to know their own member id.
        var effectivePatient = !string.IsNullOrEmpty(explicitPatient)
            ? explicitPatient
            : SmartPatientId;

        if (string.IsNullOrEmpty(effectivePatient) && string.IsNullOrEmpty(explicitId))
        {
            return Task.FromResult(FhirBadRequest(
                "ExplanationOfBenefit search requires either the patient or _id search parameter. " +
                "Provide a patient-scoped token or an explicit patient search parameter."));
        }

        var path = "fhir/ExplanationOfBenefit" + BuildUpstreamQueryString(query, effectivePatient);
        return ProxyClaimsServiceAsync("ExplanationOfBenefit", path, ct);
    }

    private static string? StripPatientPrefix(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        const string prefix = "Patient/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    /// <summary>
    /// Rewrites the inbound query string with the effective (possibly
    /// auto-injected) patient parameter. Preserves all other params
    /// verbatim so future search-parameter additions in claims-service
    /// don't require a fhir-service-side change.
    /// </summary>
    private static string BuildUpstreamQueryString(
        IQueryCollection inbound, string? effectivePatient)
    {
        var sb = new StringBuilder();
        var first = true;
        var patientWritten = false;

        foreach (var (key, values) in inbound)
        {
            if (string.Equals(key, "patient", StringComparison.OrdinalIgnoreCase))
            {
                // Replace with the effective value (may be the original
                // value or the SMART-bound id).
                if (!string.IsNullOrEmpty(effectivePatient))
                {
                    AppendParam(sb, ref first, "patient", effectivePatient);
                    patientWritten = true;
                }
                continue;
            }

            foreach (var value in values)
            {
                if (value is null) continue;
                AppendParam(sb, ref first, key, value);
            }
        }

        if (!patientWritten && !string.IsNullOrEmpty(effectivePatient))
        {
            AppendParam(sb, ref first, "patient", effectivePatient);
        }

        return sb.Length == 0 ? string.Empty : sb.ToString();
    }

    private static void AppendParam(StringBuilder sb, ref bool first, string key, string value)
    {
        sb.Append(first ? '?' : '&');
        first = false;
        sb.Append(Uri.EscapeDataString(key));
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));
    }

    private Task<IActionResult> ProxyClaimsServiceAsync(
        string resourceLabel,
        string path,
        CancellationToken ct)
        => ProxyUpstreamServiceAsync(
            _claimsServiceClient,
            "claims-service",
            resourceLabel,
            path,
            _logger,
            ct);
}
