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
    // Case-INSENSITIVE: ASP.NET route matching is, so /fhir/r4/patient/123 hits
    // the same controller as /fhir/r4/Patient/123. Matching ordinally here meant
    // a lower-cased path parsed to an unknown resource type and fell through the
    // "pass through unenforced" branch below — skipping the scope check entirely.
    // ParseResourceType returns the canonical spelling so the scope strings built
    // from it still match the token's.
    private static readonly HashSet<string> KnownResources = new(
        new[]
        {
            "Patient",
            "Coverage",
            "ExplanationOfBenefit",
            "Encounter",
            "Claim",
            "Task",
            "Communication",
            "DocumentReference",
            "ClaimResponse"
        },
        StringComparer.OrdinalIgnoreCase);

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

        var scopes = ParseScopes(context.User);

        // ── 0. System-level operations ────────────────────────────────────────
        // A path like /fhir/r4/$submit-attachment names no resource type, so the
        // resource-type parse below cannot govern it. Left unhandled it would
        // fall through the "unknown path" branch and be enforced by NOTHING —
        // an unauthenticated-scope write into a payer's record. Each system
        // operation therefore declares the resource and the access it needs.
        var systemOperation = ParseSystemOperation(context.Request.Path);
        if (systemOperation is not null)
        {
            var (operationResource, requiredAccess, contexts) = systemOperation.Value;

            if (!HasRequiredScope(scopes, operationResource, requiredAccess, contexts))
            {
                _logger.LogWarning(
                    "SMART scope denied — operation: {Operation}, required: {Resource}.{Access}",
                    SanitizeForLog(context.Request.Path.Value), operationResource, requiredAccess);

                await WriteFhirError(context, 403,
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.Forbidden,
                    "Insufficient scope. Required: "
                    + string.Join(" or ", contexts.Select(c =>
                        $"{c}/{operationResource}.{requiredAccess}")));
                return;
            }

            context.Items["SmartScopes"] = scopes;
            await _next(context);
            return;
        }

        var resourceType = ParseResourceType(context.Request.Path);
        if (resourceType == null)
        {
            // Unknown path within /fhir/r4/ — pass through
            await _next(context);
            return;
        }

        var patientClaim = context.User.FindFirst("patient")?.Value;

        // ── 1. Scope check ────────────────────────────────────────────────────
        if (!HasRequiredScope(scopes, resourceType, ReadAccess))
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
        || path.StartsWithSegments("/fhir/r4/adapter-status")
        || path.StartsWithSegments("/fhir/r4/.well-known")
        || path.StartsWithSegments("/fhir/r4/StructureDefinition")
        || path.StartsWithSegments("/fhir/r4/OperationDefinition")
        || path.StartsWithSegments("/fhir/r4/CodeSystem")
        || path.StartsWithSegments("/fhir/r4/ValueSet")
        || path.StartsWithSegments("/health")
        || path.StartsWithSegments("/swagger");

    internal const string ReadAccess = "read";
    internal const string WriteAccess = "write";

    /// <summary>
    /// System-level FHIR operations served by this server, and the scope each
    /// one needs.
    ///
    /// <c>$submit-attachment</c> is the Da Vinci CDex operation a provider uses
    /// to send documentation on a pended prior authorization. It WRITES into the
    /// payer's record, so a read scope is not enough: it is governed by the
    /// Task write scope, Task being the resource the additional-information
    /// request is projected as.
    /// </summary>
    /// <summary>
    /// Scope contexts a system operation may be invoked under. <c>$submit-attachment</c>
    /// is a provider/system transaction with a payer, so a PATIENT-context token
    /// is not an acceptable caller however it is scoped — only <c>user/</c> and
    /// <c>system/</c> grants apply.
    /// </summary>
    private static readonly string[] BackendContexts = ["user", "system"];

    private static readonly IReadOnlyDictionary<string, (string Resource, string Access, string[] Contexts)>
        SystemOperations = new Dictionary<string, (string, string, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["$submit-attachment"] = ("Task", WriteAccess, BackendContexts),
        };

    /// <summary>
    /// Recognises /fhir/r4/$operation, returning the resource and access its
    /// scope must name. Null when the path is not a system-level operation.
    /// </summary>
    private static (string Resource, string Access, string[] Contexts)? ParseSystemOperation(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not { Length: 3 }) return null;
        if (!segments[2].StartsWith('$')) return null;

        return SystemOperations.TryGetValue(segments[2], out var required) ? required : null;
    }

    /// <summary>
    /// Extracts the FHIR resource type from paths like /fhir/r4/Patient/123 → "Patient".
    /// Returns null for unrecognised paths.
    /// </summary>
    private static string? ParseResourceType(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not { Length: >= 3 })
            return null;

        // Return the CANONICAL spelling when the segment names a known resource,
        // so downstream scope strings ("user/Patient.read") are built from it
        // rather than from whatever casing the caller sent.
        return KnownResources.TryGetValue(segments[2], out var canonical)
            ? canonical
            : segments[2];
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

    /// <summary>All three scope contexts. The default for resource reads.</summary>
    private static readonly string[] AllContexts = ["patient", "user", "system"];

    /// <summary>
    /// Whether the token grants <c>{resourceType}.{access}</c> in one of the
    /// permitted contexts. A wildcard grant counts only for the access being
    /// asked for: a <c>.read</c> wildcard never satisfies a write.
    /// </summary>
    private static bool HasRequiredScope(
        HashSet<string> scopes, string resourceType, string access, string[]? contexts = null)
    {
        foreach (var context in contexts ?? AllContexts)
        {
            if (scopes.Contains($"{context}/*.{access}")
                || scopes.Contains($"{context}/*.*")
                || scopes.Contains($"{context}/{resourceType}.{access}")
                || scopes.Contains($"{context}/{resourceType}.*"))
                return true;
        }

        return false;
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
