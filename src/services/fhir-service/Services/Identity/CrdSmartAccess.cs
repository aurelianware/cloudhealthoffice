namespace FhirService.Services.Identity;

/// <summary>
/// The SMART rule for a CRD (CDS Hooks) call to <c>/cds-services/{hookId}</c>.
///
/// The hook is served outside <c>/fhir/r4</c>, so the FHIR scope middleware
/// does not see it. CDS Hooks has the EHR call with its own bearer token
/// (the token's <c>fhirAuthorization</c> in the body is only for the CDS
/// service to prefetch from the EHR's FHIR server, and is never used to
/// authorize the caller here). A CRD answer is a statement about the
/// member's coverage (prior authorization / documentation requirements), so:
///
/// <list type="number">
///   <item>The token must grant Coverage read in some context:
///   <c>{user|system|patient}/Coverage.read</c>, <c>Coverage.*</c>,
///   <c>*.read</c> or <c>*.*</c>.</item>
///   <item>A token holding ANY <c>patient/</c> scope is in a patient context
///   and must carry a patient claim, and the hook's
///   <c>context.patientId</c> must be that patient. This holds even when the
///   token also has <c>user/</c> scopes: a CRD call is not a Provider Access
///   read, so no attribution check would stand in for the binding.</item>
///   <item>A <c>user/</c> or <c>system/</c> token (the EHR or a backend) may
///   ask about any member of its tenant.</item>
/// </list>
/// The strict reading was chosen where the specs leave room: a patient-only
/// token asking about another member is refused rather than trusted.
/// </summary>
public static class CrdSmartAccess
{
    private static readonly string[] Contexts = ["user", "system", "patient"];

    /// <summary>Null when the call is allowed, otherwise the reason it is refused.</summary>
    public static string? Refusal(IReadOnlySet<string> scopes, string? boundPatient, string? hookPatient)
    {
        var coverageRead = Contexts.Any(c =>
            scopes.Contains($"{c}/Coverage.read") || scopes.Contains($"{c}/Coverage.*")
            || scopes.Contains($"{c}/*.read") || scopes.Contains($"{c}/*.*"));
        if (!coverageRead)
            return "A CRD hook needs a Coverage read scope (user/, system/ or patient/Coverage.read).";

        if (!scopes.Any(s => s.StartsWith("patient/", StringComparison.Ordinal)))
            return null;

        if (string.IsNullOrWhiteSpace(boundPatient))
            return "A patient-scoped token must carry a patient context.";

        if (string.IsNullOrWhiteSpace(hookPatient)
            || !string.Equals(Strip(hookPatient), Strip(boundPatient), StringComparison.Ordinal))
            return "The hook's patient is not the patient this token is bound to.";

        return null;
    }

    private static string Strip(string value)
        => value.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase) ? value["Patient/".Length..] : value;
}
