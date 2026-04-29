using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 InsurancePlan API controller (capability BP 5.8).
/// Thin proxy over benefit-plan-service's <c>FhirInsurancePlanController</c>;
/// benefit-plan-service owns the canonical CHO projection. fhir-service
/// remains the single FHIR façade for external consumers, mirroring the
/// 5.7/5.8/5.9 pattern for Practitioner / PractitionerRole / Organization
/// proxied to provider-service.
///
/// <para>
/// The new typed <c>HttpClient("BenefitPlanService")</c> registration in
/// <c>Program.cs</c> propagates Tenant + Correlation headers, mirroring
/// the <c>ProviderService</c> client. End-to-end Plan-Net navigation
/// works after this PR ships: an external consumer can chain
/// <c>Practitioner → PractitionerRole.organization → Organization →
/// InsurancePlan.network</c> across the two upstream services through a
/// single FHIR API surface.
/// </para>
/// </summary>
[Route("fhir/r4")]
public class InsurancePlanController : FhirControllerBase
{
    /// <summary>
    /// Back-compat alias for tests that referenced the controller-local
    /// constant before BP 5.9 extracted the canonical name into
    /// <see cref="UpstreamClientNames.BenefitPlanService"/>. New code
    /// should use the shared constant directly.
    /// </summary>
    public const string BenefitPlanServiceClientName = UpstreamClientNames.BenefitPlanService;

    private readonly HttpClient _benefitPlanServiceClient;
    private readonly ILogger<InsurancePlanController> _logger;

    public InsurancePlanController(
        IHttpClientFactory httpClientFactory,
        ILogger<InsurancePlanController> logger)
    {
        _benefitPlanServiceClient = httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);
        _logger = logger;
    }

    /// <summary>
    /// GET /fhir/r4/InsurancePlan/{id} — read InsurancePlan by PlanId.
    /// Proxies to benefit-plan-service /fhir/InsurancePlan/{id}; the
    /// upstream owns the canonical CHO projection (capability BP 5.8).
    /// The id is the operator-supplied <c>PlanId</c> per Decision 6.
    /// </summary>
    [HttpGet("InsurancePlan/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadInsurancePlan(string id, CancellationToken ct)
        => ProxyBenefitPlanServiceAsync(
            "InsurancePlan",
            $"fhir/InsurancePlan/{Uri.EscapeDataString(id)}",
            ct);

    /// <summary>
    /// GET /fhir/r4/InsurancePlan?identifier=&amp;name=&amp;status=&amp;_count=&amp;_page=
    /// — search InsurancePlans. Forwards the FHIR search query string to
    /// benefit-plan-service /fhir/InsurancePlan unchanged. The upstream
    /// honors a deliberately small subset of Plan-Net IG search
    /// parameters; <c>type</c>, <c>owned-by</c>, <c>administered-by</c>,
    /// <c>address</c> are deferred until benefit-plan-service indexes them.
    /// </summary>
    [HttpGet("InsurancePlan")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchInsurancePlans(CancellationToken ct = default)
    {
        var qs = HttpContext.Request.QueryString.HasValue
            ? HttpContext.Request.QueryString.Value
            : string.Empty;
        return ProxyBenefitPlanServiceAsync("InsurancePlan", $"fhir/InsurancePlan{qs}", ct);
    }

    private Task<IActionResult> ProxyBenefitPlanServiceAsync(
        string resourceLabel,
        string path,
        CancellationToken ct)
        => ProxyUpstreamServiceAsync(
            _benefitPlanServiceClient,
            "benefit-plan-service",
            resourceLabel,
            path,
            _logger,
            ct);
}
