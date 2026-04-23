using System.Text.Json;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Middleware;

/// <summary>
/// Enforces SMART on FHIR scope-based access control on every FHIR resource request.
///
/// Rules:
/// 1. Every resource request (except metadata and .well-known) requires a valid JWT.
/// 2. The token must contain at least one scope that grants access to the requested
///    resource type (patient/{T}.read, user/{T}.read, system/{T}.read, or wildcards).
/// 3. Patient-scoped tokens: the `patient` claim in the token is an absolute binding.
///    - GET /Patient/{id}: id MUST equal the bound patient ID.
///    - GET /{ResourceType}?patient=...: the patient param MUST match the bound ID.
///      If no patient param is provided it is auto-injected from the token.
///    - This is CMS-required: a patient token for Patient/123 must NEVER access
///      resources belonging to Patient/456.
/// </summary>
public class SmartScopeEnforcementMiddleware
{
    private static readonly JsonSerializerOptions FhirOptions =
        new JsonSerializerOptions().ForFhir(typeof(OperationOutcome).Assembly);

    // Resource types served by this FHIR service. Task, Communication,
    // DocumentReference, and ClaimResponse are added here in PR 3 as part
    // of the appeals FHIR surface — each enforces the same
    // patient|user|system/{Type}.read scope as the existing resources.
    // See AppealsController.cs in appeals-service for the domain model;
    // see FhirAppealMapper.cs in this service for the projection.
    private static readonly HashSet<string> KnownResources =
    [
        "Patient",
        "Coverage",
        "ExplanationOfBenefit",
        "Encounter",
        "Claim",
        "Task",
        "Communication",
        "DocumentReference",
        "ClaimResponse"
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<SmartScopeEnforcementMiddleware> _logger;

    public SmartScopeEnforcementMiddleware(
        RequestDelegate next,
        ILogger<SmartScopeEnforcementMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Always pass: non-FHIR paths, public FHIR paths
        if (IsPublicPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Must be authenticated
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await WriteFhirError(context, 401,
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Login,
                "Authentication required. Include a valid Bearer token.");
            return;
        }

        var resourceType = ParseResourceType(context.Request.Path);
        if (resourceType == null)
        {
            // Unknown path within /fhir/r4/ — pass through
            await _next(context);
            return;
        }

        var scopes = ParseScopes(context.User);
        var patientClaim = context.User.FindFirst("patient")?.Value;

        // ── 1. Scope check ────────────────────────────────────────────────────
        if (!HasRequiredScope(scopes, resourceType))
        {
            _logger.LogWarning(
                "SMART scope denied — resource: {Resource}, scopes: {Scopes}",
                resourceType, string.Join(" ", scopes));

            await WriteFhirError(context, 403,
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Forbidden,
                $"Insufficient scope for {resourceType}. Required: patient/{resourceType}.read, " +
                $"user/{resourceType}.read, or system/{resourceType}.read");
            return;
        }

        // ── 2. Patient binding enforcement ───────────────────────────────────
        if (!string.IsNullOrEmpty(patientClaim) && IsPatientScopedToken(scopes))
        {
            var normalizedPatient = StripPrefix("Patient/", patientClaim);

            // (a) Direct resource read — enforce ID match for Patient
            var resourceId = ParseResourceId(context.Request.Path, resourceType);
            if (resourceId != null && resourceType == "Patient" &&
                resourceId != normalizedPatient)
            {
                _logger.LogWarning(
                    "Patient binding violation — token bound to {Bound}, attempted {Requested}",
                    normalizedPatient, resourceId);

                await WriteFhirError(context, 403,
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Forbidden,
                    $"Patient token bound to Patient/{normalizedPatient} cannot access Patient/{resourceId}.");
                return;
            }

            // (b) Search — validate or auto-inject the patient parameter
            if (resourceId == null)
            {
                var queryPatient = context.Request.Query["patient"].FirstOrDefault();

                if (!string.IsNullOrEmpty(queryPatient))
                {
                    // Explicit patient param must match the token binding
                    var normalizedQuery = StripPrefix("Patient/", queryPatient);
                    if (normalizedQuery != normalizedPatient)
                    {
                        _logger.LogWarning(
                            "Patient binding violation — token bound to {Bound}, query param {Query}",
                            SanitizeForLog(normalizedPatient), SanitizeForLog(queryPatient));

                        await WriteFhirError(context, 403,
                            OperationOutcome.IssueSeverity.Error,
                            OperationOutcome.IssueType.Forbidden,
                            $"Patient token bound to Patient/{normalizedPatient} cannot search " +
                            $"resources for patient={queryPatient}.");
                        return;
                    }
                }
                // else: no patient param → controllers pick it up from SmartPatientId
            }

            context.Items["SmartPatientId"] = normalizedPatient;
        }

        context.Items["SmartScopes"] = scopes;
        await _next(context);
    }

    // ── Path analysis helpers ─────────────────────────────────────────────────

    // FHIR conformance resources (StructureDefinition, OperationDefinition,
    // CodeSystem, ValueSet) sit at the same metadata/discovery layer as the
    // CapabilityStatement. Clients need to discover them before authenticating,
    // so they bypass SMART scope enforcement just like /fhir/r4/metadata.
    private static bool IsPublicPath(PathString path)
        => !path.StartsWithSegments("/fhir/r4")
        || path.StartsWithSegments("/fhir/r4/metadata")
        || path.StartsWithSegments("/fhir/r4/.well-known")
        || path.StartsWithSegments("/fhir/r4/StructureDefinition")
        || path.StartsWithSegments("/fhir/r4/OperationDefinition")
        || path.StartsWithSegments("/fhir/r4/CodeSystem")
        || path.StartsWithSegments("/fhir/r4/ValueSet")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/swagger");

    /// <summary>
    /// Extracts the FHIR resource type from paths like /fhir/r4/Patient/123 → "Patient".
    /// Returns null for unrecognised paths.
    /// </summary>
    private static string? ParseResourceType(PathString path)
    {
        // path = /fhir/r4/{ResourceType}[/{id}]
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments == null || segments.Length < 3) return null;  // ["fhir","r4",...]

        var candidate = segments[2]; // index 0="fhir", 1="r4", 2=resourceType
        return KnownResources.Contains(candidate) ? candidate : null;
    }

    /// <summary>Returns the resource ID from /fhir/r4/{Type}/{id}, or null for searches.</summary>
    private static string? ParseResourceId(PathString path, string resourceType)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments == null || segments.Length < 4) return null;
        return segments.Length >= 4 ? segments[3] : null;
    }

    // ── Scope analysis helpers ────────────────────────────────────────────────

    private static HashSet<string> ParseScopes(System.Security.Claims.ClaimsPrincipal user)
    {
        // OpenIddict emits scopes as space-separated values in a single claim,
        // or as multiple "scope" claims — handle both.
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in user.FindAll("scope"))
            foreach (var s in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                result.Add(s);
        return result;
    }

    private static bool HasRequiredScope(HashSet<string> scopes, string resourceType)
    {
        if (scopes.Contains("patient/*.read") || scopes.Contains("user/*.read") ||
            scopes.Contains("system/*.read"))
            return true;

        return scopes.Contains($"patient/{resourceType}.read")
            || scopes.Contains($"user/{resourceType}.read")
            || scopes.Contains($"system/{resourceType}.read");
    }

    /// <summary>
    /// A token is patient-scoped when it carries patient/* or patient/{T}.read
    /// but NOT user/* or system/* (those are broader grants).
    /// </summary>
    private static bool IsPatientScopedToken(HashSet<string> scopes)
    {
        if (scopes.Contains("user/*.read") || scopes.Contains("system/*.read"))
            return false;

        return scopes.Contains("patient/*.read")
            || scopes.Any(s => s.StartsWith("patient/", StringComparison.Ordinal));
    }

    // ── OperationOutcome error writer ─────────────────────────────────────────

    private static async Task WriteFhirError(
        HttpContext context, int statusCode,
        OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code,
        string diagnostics)
    {
        var outcome = new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = severity,
                    Code = code,
                    Diagnostics = diagnostics
                }
            ]
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/fhir+json; charset=utf-8";
        var json = JsonSerializer.Serialize(outcome, FhirOptions);
        await context.Response.WriteAsync(json);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private static string StripPrefix(string prefix, string value)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
}
