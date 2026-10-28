using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Logging;
using CloudHealthOffice.Infrastructure.Security;
using FhirService.Services;
using FhirService.Services.PublicDirectory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SharedTenantMiddleware = CloudHealthOffice.Infrastructure.Middleware.TenantMiddleware;

namespace FhirService.Controllers;

/// <summary>
/// The public Provider Directory API (CMS-9115-F, D-29): read and search of a
/// plan's Practitioner, PractitionerRole, Organization and InsurancePlan
/// resources with no user authentication, as CMS requires.
///
/// The plan is named by the public directory id in the URL
/// (<c>/fhir/r4/directory/{directory}/...</c>), which maps to a tenant only
/// through <see cref="PublicProviderDirectoryOptions"/>: a plan that has not
/// published its directory has none here. That is the one place in fhir-service
/// where a tenant is chosen by something other than a validated token, so the
/// route serves directory data and nothing else:
///
///   * Any credential the request carries is ignored. The caller is treated as
///     anonymous for the whole request, so no token's tenant or scopes shape
///     the answer and no caller's credential travels upstream.
///   * provider-service and benefit-plan-service are called as fhir-service,
///     for the directory's tenant only.
///   * Every response passes through <see cref="PublicDirectoryResponseFilter"/>,
///     which removes CHO-internal extensions (such as the integrity score), tax
///     ids and inactive providers.
///   * Requests are rate limited per client address.
///
/// The authenticated directory on <c>/fhir/r4/Practitioner</c> and friends is
/// unchanged. Location stays on that route only: it is served from NPPES, not
/// from the plan's own network data.
/// </summary>
[Route("fhir/r4/directory/{directory}")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
[TypeFilter(typeof(PublicDirectoryAccessFilter))]
public sealed class PublicProviderDirectoryController : FhirControllerBase
{
    public const string RateLimitPolicy = "public-provider-directory";

    private readonly HttpClient _providerService;
    private readonly HttpClient _benefitPlanService;
    private readonly ILogger<PublicProviderDirectoryController> _logger;

    public PublicProviderDirectoryController(
        IHttpClientFactory httpClientFactory,
        ILogger<PublicProviderDirectoryController> logger)
    {
        _providerService = httpClientFactory.CreateClient("ProviderService");
        _benefitPlanService = httpClientFactory.CreateClient(UpstreamClientNames.BenefitPlanService);
        _logger = logger;
    }

    [HttpGet("Practitioner/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadPractitioner(string id, CancellationToken ct)
        => FetchAsync(_providerService, "Practitioner", $"fhir/Practitioner/{Uri.EscapeDataString(id)}", id, ct);

    [HttpGet("Practitioner")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchPractitioners(CancellationToken ct = default)
        => FetchAsync(_providerService, "Practitioner", $"fhir/Practitioner{Query}", null, ct);

    [HttpGet("PractitionerRole/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadPractitionerRole(string id, CancellationToken ct)
        => FetchAsync(_providerService, "PractitionerRole", $"fhir/PractitionerRole/{Uri.EscapeDataString(id)}", id, ct);

    [HttpGet("PractitionerRole")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchPractitionerRoles(CancellationToken ct = default)
        => FetchAsync(_providerService, "PractitionerRole", $"fhir/PractitionerRole{Query}", null, ct);

    [HttpGet("Organization/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadOrganization(string id, CancellationToken ct)
        => FetchAsync(_providerService, "Organization", $"fhir/Organization/{Uri.EscapeDataString(id)}", id, ct);

    [HttpGet("Organization")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchOrganizations(CancellationToken ct = default)
        => FetchAsync(_providerService, "Organization", $"fhir/Organization{Query}", null, ct);

    [HttpGet("InsurancePlan/{id}")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> ReadInsurancePlan(string id, CancellationToken ct)
        => FetchAsync(_benefitPlanService, "InsurancePlan", $"fhir/InsurancePlan/{Uri.EscapeDataString(id)}", id, ct);

    [HttpGet("InsurancePlan")]
    [Produces("application/fhir+json")]
    public Task<IActionResult> SearchInsurancePlans(CancellationToken ct = default)
        => FetchAsync(_benefitPlanService, "InsurancePlan", $"fhir/InsurancePlan{Query}", null, ct);

    private string Query => HttpContext.Request.QueryString.HasValue ? HttpContext.Request.QueryString.Value! : string.Empty;

    private async Task<IActionResult> FetchAsync(
        HttpClient upstream, string resourceType, string path, string? id, CancellationToken ct)
    {
        var loggablePath = LogSanitizer.Sanitize(path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path).UseChoServiceIdentity();
            request.Headers.Add(SharedTenantMiddleware.TenantHeaderName, PublicDirectoryAccessFilter.Tenant(HttpContext));
            using var response = await upstream.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return FhirNotFound(resourceType, id ?? string.Empty);
            if (!response.IsSuccessStatusCode)
            {
                // Upstream refusals (including 401/403 when the service token
                // could not be minted) are not the public caller's doing, and
                // their bodies are not for the public.
                _logger.LogWarning(
                    "Public directory {Resource} upstream returned {Status} for {Path}",
                    resourceType, (int)response.StatusCode, loggablePath);
                return (int)response.StatusCode == 400
                    ? FhirBadRequest($"The {resourceType} search could not be processed.")
                    : FhirBadGateway($"{resourceType} directory is unavailable.");
            }

            var filtered = PublicDirectoryResponseFilter.Filter(body);
            if (filtered is null)
                return FhirNotFound(resourceType, id ?? string.Empty);

            return new ContentResult { Content = filtered, ContentType = "application/fhir+json", StatusCode = 200 };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Public directory {Resource} upstream unreachable for {Path}", resourceType, loggablePath);
            return FhirBadGateway($"{resourceType} directory is unreachable.");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Public directory {Resource} upstream timed out for {Path}", resourceType, loggablePath);
            return FhirBadGateway($"{resourceType} directory timed out.");
        }
    }
}

/// <summary>
/// Resolves the public directory id to its tenant before the action runs, and
/// makes the caller anonymous for the rest of the request. See
/// <see cref="PublicProviderDirectoryController"/>.
/// </summary>
public sealed class PublicDirectoryAccessFilter : IAsyncActionFilter
{
    private const string TenantItemKey = "PublicDirectoryTenant";

    private readonly PublicProviderDirectoryOptions _options;

    public PublicDirectoryAccessFilter(IOptions<PublicProviderDirectoryOptions> options) => _options = options.Value;

    /// <summary>The tenant of the directory this request reads.</summary>
    public static string Tenant(HttpContext http)
        => http.Items[TenantItemKey] as string
           ?? throw new InvalidOperationException("No public directory was resolved for this request.");

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var directory = context.RouteData.Values["directory"] as string;
        var tenant = _options.ResolveTenant(directory);
        if (tenant is null)
        {
            context.Result = new NotFoundObjectResult(new Hl7.Fhir.Model.OperationOutcome
            {
                Issue =
                [
                    new Hl7.Fhir.Model.OperationOutcome.IssueComponent
                    {
                        Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                        Code = Hl7.Fhir.Model.OperationOutcome.IssueType.NotFound,
                        Diagnostics = "No public provider directory is published under this name.",
                    },
                ],
            });
            return;
        }

        var http = context.HttpContext;
        http.User = new ClaimsPrincipal(new ClaimsIdentity());
        http.Items.Remove("TenantId");
        http.Items[TenantItemKey] = tenant;
        await next();
    }
}
