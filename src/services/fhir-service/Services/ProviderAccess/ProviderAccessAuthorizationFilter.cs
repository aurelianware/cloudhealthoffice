using FhirService.Middleware;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Text.Json;

namespace FhirService.Services.ProviderAccess;

/// <summary>
/// Enforces Provider Access authorization for every member-scoped FHIR read, in
/// one place.
///
/// WHY A FILTER, NOT MIDDLEWARE. Provider Access needs the tenant, and
/// <c>TenantMiddleware</c> runs AFTER <c>SmartScopeEnforcementMiddleware</c> in
/// the pipeline; a middleware placed with the SMART check would have no tenant to
/// isolate on. An MVC filter runs after the whole middleware pipeline, so
/// authentication, SMART scope, and tenant are all established facts by the time
/// it executes — and it still runs BEFORE any action body, so an unauthorized
/// request cannot assemble or retrieve member PHI. This is the narrowest shared
/// boundary that reliably covers all of them.
///
/// ORDER OF CONTROLS. authentication -> SMART scope (both upstream middleware)
/// -> attribution -> Provider Access consent (both here) -> action body reads
/// member data. All four are independent and mandatory.
///
/// WHAT IT GOVERNS. Provider Access is the caller shape, not a route name: a
/// token carrying <c>user/</c> or <c>system/</c> scopes is a provider or backend
/// service reading someone else's record. A patient-scoped token is Patient
/// Access — the member reading their own data — which a Provider Access consent
/// does not govern and must not be required for.
/// </summary>
public sealed class ProviderAccessAuthorizationFilter : IAsyncActionFilter
{
    /// <summary>
    /// Member-scoped resource types this filter governs. Every resource the SMART
    /// layer knows about is member-scoped and therefore listed here; a structural
    /// test pins the two sets together so a resource added to one cannot quietly
    /// escape the other.
    /// </summary>
    public static readonly IReadOnlySet<string> GovernedResources =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Patient",
            "Coverage",
            "ExplanationOfBenefit",
            "Encounter",
            "Claim",
            "Task",
            "Communication",
            "DocumentReference",
            "ClaimResponse",
        };

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(OperationOutcome).Assembly);

    private readonly IProviderAccessAuthorizationService _authorization;
    private readonly ILogger<ProviderAccessAuthorizationFilter> _logger;

    public ProviderAccessAuthorizationFilter(
        IProviderAccessAuthorizationService authorization,
        ILogger<ProviderAccessAuthorizationFilter> logger)
    {
        _authorization = authorization;
        _logger = logger;
    }

    // System.Threading.Tasks.Task spelled out: `using Hl7.Fhir.Model` puts the
    // FHIR Task resource in scope, and this file needs both.
    public async System.Threading.Tasks.Task OnActionExecutionAsync(
        ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;

        // Provider Access is a READ api. Operations (POST .../$member-match,
        // $member-data-export) are a different surface with their own
        // authorization — Payer-to-Payer runs its own consent gate for the
        // PayerToPayerExchange purpose — and must not be re-judged against a
        // Provider Access panel, where the operation name would be mistaken for
        // a member id.
        if (!HttpMethods.IsGet(http.Request.Method))
        {
            await next();
            return;
        }

        var resourceType = ParseResourceType(http.Request.Path);
        if (resourceType is null || !GovernedResources.Contains(resourceType))
        {
            await next();
            return;
        }

        var pathId = ParseResourceId(http.Request.Path);
        if (pathId is not null && pathId.StartsWith('$'))
        {
            await next();
            return;
        }

        var scopes = http.Items["SmartScopes"] as HashSet<string> ?? new HashSet<string>();

        // Patient Access (the member reading their own record) is governed by the
        // patient binding SMART already enforced, not by Provider Access consent.
        if (!IsProviderShapedCall(scopes))
        {
            await next();
            return;
        }

        var tenantId = http.GetTenantId() ?? string.Empty;
        var providerId = ResolveCallerId(http);
        var memberId = ResolveMemberId(http, resourceType);

        var decision = await _authorization.AuthorizeAsync(
            new ProviderAccessRequest
            {
                TenantId = tenantId,
                MemberId = memberId ?? string.Empty,
                ProviderId = providerId,
            },
            http.RequestAborted);

        Audit(decision, resourceType);

        if (!decision.Allowed)
        {
            context.Result = Forbidden();
            return;
        }

        await next();
    }

    /// <summary>
    /// The calling provider or backend client, from the validated token.
    /// The JWT bearer handler maps <c>sub</c> onto
    /// <see cref="System.Security.Claims.ClaimTypes.NameIdentifier"/> by default,
    /// so both spellings are checked; <c>client_id</c>/<c>azp</c> cover backend
    /// service tokens issued without a subject. Never read from a header or the
    /// query string — the caller does not get to name itself.
    /// </summary>
    private static string? ResolveCallerId(HttpContext http)
    {
        var user = http.User;
        if (user is null)
            return null;

        foreach (var claim in new[]
                 {
                     "sub",
                     System.Security.Claims.ClaimTypes.NameIdentifier,
                     "client_id",
                     "azp",
                 })
        {
            var value = user.FindFirst(claim)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return user.Identity?.Name;
    }

    /// <summary>
    /// A provider-shaped call carries <c>user/</c> or <c>system/</c> scopes. This
    /// is the caller's authorization shape, established by the token — not a
    /// route-name check and not a per-controller opt-in.
    /// </summary>
    private static bool IsProviderShapedCall(HashSet<string> scopes)
        => scopes.Any(s => s.StartsWith("user/", StringComparison.Ordinal)
                        || s.StartsWith("system/", StringComparison.Ordinal));

    /// <summary>
    /// The member this request is about. <c>Patient/{id}</c> names the member
    /// directly; everything else must carry an explicit member context, because a
    /// resource id alone cannot be resolved to a member without first reading the
    /// resource — which is exactly the PHI access being authorized. No member
    /// context therefore denies rather than guesses.
    /// </summary>
    private static string? ResolveMemberId(HttpContext http, string resourceType)
    {
        if (string.Equals(resourceType, "Patient", StringComparison.Ordinal))
        {
            var id = ParseResourceId(http.Request.Path);
            if (!string.IsNullOrWhiteSpace(id))
                return id;
        }

        var queryPatient = http.Request.Query["patient"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(queryPatient))
            return StripPrefix("Patient/", queryPatient);

        return http.Items["SmartPatientId"] as string;
    }

    /// <summary>
    /// One uniform refusal for every denial category. A caller must not be able
    /// to tell "not attributed" from "no consent" from "no such member": that
    /// difference is exactly what member enumeration needs. The structured
    /// category is kept in the audit record instead.
    /// </summary>
    private static IActionResult Forbidden()
    {
        var outcome = new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = OperationOutcome.IssueType.Forbidden,
                    Diagnostics = "Provider Access is not authorized for this request.",
                }
            ]
        };

        return new ContentResult
        {
            StatusCode = 403,
            ContentType = "application/fhir+json; charset=utf-8",
            Content = JsonSerializer.Serialize(outcome, FhirJson),
        };
    }

    /// <summary>
    /// Records the decision with PHI-free identifiers only: tenant, member id,
    /// caller id, consent id, category, and the evaluation instant. No
    /// demographics, no clinical payload, no consent narrative, no token.
    /// </summary>
    private void Audit(ProviderAccessDecision decision, string resourceType)
    {
        if (decision.Allowed)
        {
            _logger.LogInformation(
                "Provider Access granted: tenant={Tenant} provider={Provider} member={Member} "
                + "resource={Resource} consent={Consent} at={At}",
                Clean(decision.TenantId), Clean(decision.ProviderId), Clean(decision.MemberId),
                Clean(resourceType), Clean(decision.AuthorizingConsentId), decision.EvaluatedAtUtc);
            return;
        }

        _logger.LogWarning(
            "Provider Access denied: tenant={Tenant} provider={Provider} member={Member} "
            + "resource={Resource} reason={Reason} attributed={Attributed} consent={ConsentReason} at={At}",
            Clean(decision.TenantId), Clean(decision.ProviderId), Clean(decision.MemberId),
            Clean(resourceType), decision.Reason, decision.Attributed,
            Clean(decision.ConsentDecisionReason), decision.EvaluatedAtUtc);
    }

    private static string? ParseResourceType(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /fhir/r4/{ResourceType}[/{id}]
        return segments is { Length: >= 3 } ? segments[2] : null;
    }

    private static string? ParseResourceId(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is { Length: >= 4 } ? segments[3] : null;
    }

    private static string StripPrefix(string prefix, string value)
        => value.StartsWith(prefix, StringComparison.Ordinal)
            ? value[prefix.Length..]
            : value;

    /// <summary>Strips CR/LF so an id cannot forge a log entry (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
