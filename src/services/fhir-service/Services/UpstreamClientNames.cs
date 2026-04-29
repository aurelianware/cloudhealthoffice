namespace FhirService.Services;

/// <summary>
/// Names of the typed <see cref="System.Net.Http.HttpClient"/> instances
/// fhir-service registers via <c>IHttpClientFactory</c> for upstream
/// service proxies (capabilities 5.7+ provider-service hops, BP 5.8+
/// benefit-plan-service hops). Centralised so individual controllers can
/// reuse the names without taking a dependency on a sibling controller's
/// implementation detail (Copilot review BP 5.9).
/// </summary>
internal static class UpstreamClientNames
{
    /// <summary>
    /// Typed HttpClient for benefit-plan-service. Used by
    /// <see cref="Controllers.InsurancePlanController"/> (BP 5.8) and
    /// <see cref="Controllers.EndpointController"/> (BP 5.9).
    /// </summary>
    public const string BenefitPlanService = "BenefitPlanService";
}
