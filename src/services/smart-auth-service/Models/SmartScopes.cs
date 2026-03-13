namespace SmartAuthService.Models;

/// <summary>
/// SMART App Launch Framework v2 scope constants.
/// https://hl7.org/fhir/smart-app-launch/scopes-and-launch-context.html
/// </summary>
public static class SmartScopes
{
    // ── Standard OIDC ─────────────────────────────────────────────────────────
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string Email = "email";
    public const string FhirUser = "fhirUser";

    // ── Launch context ────────────────────────────────────────────────────────
    public const string Launch = "launch";
    public const string LaunchPatient = "launch/patient";
    public const string LaunchEncounter = "launch/encounter";

    // ── Wildcard resource scopes ──────────────────────────────────────────────
    public const string PatientWildcardRead = "patient/*.read";
    public const string UserWildcardRead = "user/*.read";
    public const string SystemWildcardRead = "system/*.read";

    // ── Patient-level resource scopes ─────────────────────────────────────────
    public const string PatientPatientRead = "patient/Patient.read";
    public const string PatientCoverageRead = "patient/Coverage.read";
    public const string PatientEobRead = "patient/ExplanationOfBenefit.read";
    public const string PatientEncounterRead = "patient/Encounter.read";
    public const string PatientClaimRead = "patient/Claim.read";

    // ── User-level resource scopes ────────────────────────────────────────────
    public const string UserPatientRead = "user/Patient.read";
    public const string UserCoverageRead = "user/Coverage.read";
    public const string UserEobRead = "user/ExplanationOfBenefit.read";
    public const string UserEncounterRead = "user/Encounter.read";
    public const string UserClaimRead = "user/Claim.read";

    // ── System-level resource scopes ──────────────────────────────────────────
    public const string SystemPatientRead = "system/Patient.read";
    public const string SystemCoverageRead = "system/Coverage.read";
    public const string SystemEobRead = "system/ExplanationOfBenefit.read";
    public const string SystemEncounterRead = "system/Encounter.read";
    public const string SystemClaimRead = "system/Claim.read";

    /// <summary>
    /// Returns all scopes that grant read access to a given FHIR resource type
    /// for a given level (patient / user / system).
    /// </summary>
    public static IEnumerable<string> ForResource(string resourceType)
    {
        var wildcard = new[] { PatientWildcardRead, UserWildcardRead, SystemWildcardRead };
        var specific = resourceType switch
        {
            "Patient" => new[]
            {
                PatientPatientRead, UserPatientRead, SystemPatientRead
            },
            "Coverage" => new[]
            {
                PatientCoverageRead, UserCoverageRead, SystemCoverageRead
            },
            "ExplanationOfBenefit" => new[]
            {
                PatientEobRead, UserEobRead, SystemEobRead
            },
            "Encounter" => new[]
            {
                PatientEncounterRead, UserEncounterRead, SystemEncounterRead
            },
            "Claim" => new[]
            {
                PatientClaimRead, UserClaimRead, SystemClaimRead
            },
            _ => Array.Empty<string>()
        };
        return wildcard.Concat(specific);
    }
}

/// <summary>SMART token claim names.</summary>
public static class SmartClaims
{
    /// <summary>Bound patient ID — present on patient-scoped tokens.</summary>
    public const string Patient = "patient";

    /// <summary>Bound encounter ID — present on EHR-launched tokens.</summary>
    public const string Encounter = "encounter";

    /// <summary>FHIR user URL — e.g. Practitioner/prov-001.</summary>
    public const string FhirUser = "fhirUser";
}
