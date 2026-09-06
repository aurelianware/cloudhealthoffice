using System.Text.Json;
using FhirService.Services.Clinical;
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
    //
    // The USCDI clinical types (PAT-02) are appended from
    // ClinicalResourceInventory rather than retyped: a clinical resource that
    // this set did not know about would fall through to the "unknown path"
    // branch below and be served with NO scope check at all, so the two must be
    // the same list by construction, not by discipline.
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
        }.Concat(ClinicalResourceInventory.ResourceTypes),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Search parameters that name the member a request is about. A patient-scoped
    /// token's binding is enforced against every one of them.
    /// </summary>
    private static readonly string[] MemberBindingParameters = ["patient", "subject"];

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

        // What this request actually IS: which resource's scope governs it,
        // whether it READS or WRITES, and which scope contexts may invoke it.
        // Resolved once, from the path AND the method — enforcing `.read` on
        // everything, as this middleware previously did, let any write reachable
        // under /fhir/r4 through on a read scope.
        var interaction = ResolveInteraction(context.Request);
        if (interaction == null)
        {
            // Unknown path within /fhir/r4/ — pass through
            await _next(context);
            return;
        }

        var resourceType = interaction.Resource;
        var patientClaim = context.User.FindFirst("patient")?.Value;

        // ── 1. Scope check ────────────────────────────────────────────────────
        if (!HasRequiredScope(scopes, resourceType, interaction.Access, interaction.Contexts))
        {
            _logger.LogWarning(
                "SMART scope denied — resource: {Resource}, access: {Access}, scopes: {Scopes}",
                resourceType, interaction.Access, string.Join(" ", scopes));

            await WriteFhirError(context, 403,
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Forbidden,
                $"Insufficient scope for {resourceType}. Required: "
                + string.Join(", ", interaction.Contexts.Select(
                    c => $"{c}/{resourceType}.{interaction.Access}")));
            return;
        }

        // ── 2. Patient binding enforcement ───────────────────────────────────
        if (!string.IsNullOrEmpty(patientClaim) && IsPatientScopedToken(scopes))
        {
            var normalizedPatient = StripPrefix("Patient/", patientClaim);

            // (a) Direct resource read — enforce ID match for Patient
            var resourceId = interaction.ResourceId;
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

            // (b) Search — validate or auto-inject the member parameter.
            //     EVERY parameter that names a member is checked, not just
            //     `patient`: the clinical resources added for PAT-02 are also
            //     searchable by `subject`, and checking one while ignoring the
            //     other would leave the second as an unguarded way to ask for
            //     somebody else's record.
            if (resourceId == null)
            {
                foreach (var parameter in MemberBindingParameters)
                {
                    var queryMember = context.Request.Query[parameter].FirstOrDefault();
                    if (string.IsNullOrEmpty(queryMember)) continue;

                    var normalizedQuery = StripPrefix("Patient/", queryMember);
                    if (normalizedQuery == normalizedPatient) continue;

                    _logger.LogWarning(
                        "Patient binding violation — token bound to {Bound}, {Parameter}={Query}",
                        SanitizeForLog(normalizedPatient), SanitizeForLog(parameter),
                        SanitizeForLog(queryMember));

                    await WriteFhirError(context, 403,
                        OperationOutcome.IssueSeverity.Error,
                        OperationOutcome.IssueType.Forbidden,
                        $"Patient token bound to Patient/{normalizedPatient} cannot search " +
                        $"resources for {parameter}={queryMember}.");
                    return;
                }
                // else: no member param → controllers pick it up from SmartPatientId
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

    /// <summary>All three SMART scope contexts. The default for most interactions.</summary>
    private static readonly string[] AllContexts = ["patient", "user", "system"];

    /// <summary>
    /// Backend-only contexts. A provider/payer transaction is not something a
    /// PATIENT-context token may invoke, however it is scoped.
    /// </summary>
    private static readonly string[] BackendContexts = ["user", "system"];

    /// <summary>
    /// What one request is, for authorization purposes: the resource whose scope
    /// governs it, the instance it names (for patient binding), whether it reads
    /// or writes, and which scope contexts may invoke it.
    /// </summary>
    private sealed record FhirInteraction(
        string Resource, string? ResourceId, string Access, string[] Contexts);

    /// <summary>
    /// SYSTEM-level FHIR operations — <c>/fhir/r4/$operation</c> — and the scope
    /// each needs. A path like this names no resource type, so without an entry
    /// here there is nothing for a scope string to be built from.
    ///
    /// <c>$submit-attachment</c> is the Da Vinci CDex operation a provider uses
    /// to send documentation on a pended prior authorization. It WRITES into the
    /// payer's record, so it is governed by the Task write scope — Task being the
    /// resource the additional-information request is projected as — and only in
    /// a backend context.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (string Resource, string Access, string[] Contexts)>
        SystemOperations = new Dictionary<string, (string, string, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["$submit-attachment"] = ("Task", WriteAccess, BackendContexts),

            // Appeal submission creates an appeal, which this surface projects as
            // a Task. A write, therefore — it was previously reachable with a
            // read scope.
            ["$cho-appeal-submit"] = ("Task", WriteAccess, AllContexts),

            // Bulk export ASKS for data. POST is the Bulk Data IG's kick-off
            // shape, not evidence of a write.
            ["$export"] = ("$export", ReadAccess, AllContexts),
        };

    /// <summary>
    /// Named operations on a resource, and the access each needs.
    ///
    /// A FHIR operation's HTTP method says nothing about whether it reads or
    /// writes — <c>$inquire</c> and <c>$member-match</c> are POSTs that read —
    /// so every operation this server serves is classified HERE rather than
    /// inferred. An operation that is NOT listed falls back to the method, so a
    /// new POST operation defaults to requiring a write scope rather than
    /// silently inheriting read-only enforcement.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ResourceOperations =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Da Vinci PAS. $submit CREATES a prior authorization; $inquire only
            // projects the one it already wrote.
            ["Claim/$submit"] = WriteAccess,
            ["Claim/$inquire"] = ReadAccess,

            // Payer-to-Payer. A match and an export are reads; initiating an
            // outbound exchange is an action.
            ["Patient/$member-match"] = ReadAccess,
            ["PayerToPayer/$member-data-export"] = ReadAccess,
            ["PayerToPayer/$initiate"] = WriteAccess,

            // DTR: a questionnaire package is assembled and returned, not stored.
            ["Questionnaire/$questionnaire-package"] = ReadAccess,

            // Bulk export for a group — the kick-off shape again.
            ["Group/$export"] = ReadAccess,
        };

    /// <summary>
    /// Resolves a request under <c>/fhir/r4</c> into the interaction that governs
    /// it. Null only when the path is too short to name anything.
    ///
    /// Nothing here falls through to "unenforced": an operation nobody
    /// classified is still governed by a scope named for the operation itself,
    /// with the access its HTTP method implies.
    /// </summary>
    private static FhirInteraction? ResolveInteraction(HttpRequest request)
    {
        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not { Length: >= 3 })
            return null;

        var methodAccess = AccessForMethod(request.Method);

        // /fhir/r4/$operation[/...]
        if (segments[2].StartsWith('$'))
        {
            return SystemOperations.TryGetValue(segments[2], out var declared)
                ? new FhirInteraction(declared.Resource, null, declared.Access, declared.Contexts)
                : new FhirInteraction(segments[2], null, methodAccess, AllContexts);
        }

        // The CANONICAL spelling when the segment names a known resource, so the
        // scope strings are built from it rather than from the caller's casing.
        var resource = KnownResources.TryGetValue(segments[2], out var canonical)
            ? canonical
            : segments[2];

        // /fhir/r4/{Resource}/$operation
        if (segments.Length >= 4 && segments[3].StartsWith('$'))
            return new FhirInteraction(resource, null, OperationAccess(resource, segments[3], methodAccess), AllContexts);

        // /fhir/r4/{Resource}/{id}/$operation
        if (segments.Length >= 5 && segments[4].StartsWith('$'))
            return new FhirInteraction(resource, segments[3], OperationAccess(resource, segments[4], methodAccess), AllContexts);

        // Plain REST: /fhir/r4/{Resource}[/{id}]
        var id = segments.Length >= 4 ? segments[3] : null;
        return new FhirInteraction(resource, id, methodAccess, AllContexts);
    }

    private static string OperationAccess(string resource, string operation, string fallback)
        => ResourceOperations.TryGetValue($"{resource}/{operation}", out var declared)
            ? declared
            : fallback;

    /// <summary>
    /// The access a plain REST interaction implies. Safe methods read; everything
    /// else writes. Operations do NOT use this as their first answer — see
    /// <see cref="ResourceOperations"/>.
    /// </summary>
    private static string AccessForMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
            ? ReadAccess
            : WriteAccess;

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

    // Delegates to the shared writer so every refusal under /fhir/r4 — whichever
    // middleware issues it — has the same OperationOutcome shape.
    private static Task WriteFhirError(
        HttpContext context, int statusCode,
        OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code,
        string diagnostics)
        => FhirErrorResponse.WriteAsync(context, statusCode, severity, code, diagnostics);

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private static string StripPrefix(string prefix, string value)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
}
