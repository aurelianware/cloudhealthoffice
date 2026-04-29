using FhirService.Services;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// FHIR R4 Endpoint API controller (capability BP 5.9 — Plan Documents →
/// FHIR Endpoint projection). Thin proxy over benefit-plan-service's
/// <c>FhirEndpointController</c>; benefit-plan-service owns the canonical
/// CHO Endpoint projection. Mirrors
/// <see cref="InsurancePlanController"/> byte-for-byte modulo the route.
///
/// <para>
/// Reuses the existing <c>HttpClient("BenefitPlanService")</c> typed
/// client registration in <c>Program.cs</c> — the same client that
/// proxies <c>InsurancePlan</c>. Tenant + Correlation header propagation
/// already configured there flows verbatim.
/// </para>
/// </summary>
[Route("fhir/r4")]
public class EndpointController : FhirControllerBase
{
    private readonly HttpClient _benefitPlanServiceClient;
    private readonly ILogger<EndpointController> _logger;

    public EndpointController(
        IHttpClientFactory httpClientFactory,
        ILogger<EndpointController> logger)
    {
        // Pull the typed HttpClient name from UpstreamClientNames so the
        // controller doesn't take a dependency on a sibling controller's
        // implementation detail. Copilot review BP 5.9.
        _benefitPlanServiceClient =
            httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);
        _logger = logger;
    }

    /// <summary>
    /// GET /fhir/r4/Endpoint/{id} — read Endpoint by
    /// <see cref="BenefitPlanService.Models.PlanDocumentReference.Id"/>.
    /// Proxies to benefit-plan-service /fhir/Endpoint/{id}; the upstream
    /// owns the canonical CHO projection (capability BP 5.9 Decision 2).
    /// </summary>
    [HttpGet("Endpoint/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadEndpoint(string id, CancellationToken ct)
        => ProxyBenefitPlanServiceAsync(
            "Endpoint",
            $"fhir/Endpoint/{Uri.EscapeDataString(id)}",
            ct);

    /// <summary>
    /// GET /fhir/r4/Endpoint?_id=&amp;status=&amp;connection-type=&amp;_count=&amp;_page=
    /// — search Endpoints. Forwards the FHIR search query string to
    /// benefit-plan-service /fhir/Endpoint unchanged. The upstream honors
    /// <c>_id</c>, <c>status</c>, and <c>connection-type</c>;
    /// <c>organization=</c> is deferred (no Organization→Endpoint link
    /// today).
    /// </summary>
    [HttpGet("Endpoint")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchEndpoints(CancellationToken ct = default)
    {
        var qs = HttpContext.Request.QueryString.HasValue
            ? HttpContext.Request.QueryString.Value
            : string.Empty;
        return ProxyBenefitPlanServiceAsync("Endpoint", $"fhir/Endpoint{qs}", ct);
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
