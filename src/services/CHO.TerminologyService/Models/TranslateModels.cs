namespace CHO.TerminologyService.Models;

/// <summary>
/// Request model for ConceptMap/$translate.
/// Follows the FHIR Parameters resource pattern.
/// </summary>
public class TranslateRequest
{
    /// <summary>Source coding system URI</summary>
    public string System { get; set; } = string.Empty;

    /// <summary>Code to translate</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Target coding system URI</summary>
    public string TargetSystem { get; set; } = string.Empty;

    /// <summary>Optional patient context for rule-based disambiguation</summary>
    public PatientContext? Context { get; set; }

    /// <summary>Tenant ID for plan-specific override lookup</summary>
    public string? TenantId { get; set; }
}

/// <summary>
/// Patient context provided to the rule engine for disambiguating one-to-many mappings.
/// </summary>
public class PatientContext
{
    public int? AgeInYears { get; set; }
    public string? Gender { get; set; }
    public List<string>? ActiveConditions { get; set; }
    public string? StateCode { get; set; }
}

/// <summary>
/// Response model for ConceptMap/$translate.
/// Follows the FHIR Parameters resource pattern.
/// </summary>
public class TranslateResponse
{
    /// <summary>Whether a mapping was found</summary>
    public bool Result { get; set; }

    /// <summary>Human-readable message (especially when Result is false)</summary>
    public string? Message { get; set; }

    /// <summary>The matched mappings (may be multiple for one-to-many)</summary>
    public List<TranslateMatch> Matches { get; set; } = new();

    /// <summary>Which map version was used (for audit)</summary>
    public string? MapVersionId { get; set; }

    /// <summary>Timestamp of the translation</summary>
    public DateTime TranslatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single translation match.
/// </summary>
public class TranslateMatch
{
    /// <summary>FHIR equivalence type</summary>
    public string Equivalence { get; set; } = "equivalent";

    /// <summary>The translated code</summary>
    public TranslatedCoding Concept { get; set; } = new();

    /// <summary>Whether this match was selected by context rules vs. all candidates</summary>
    public bool IsContextResolved { get; set; }

    /// <summary>Whether this came from a plan-specific override</summary>
    public bool IsOverride { get; set; }

    /// <summary>The source of this mapping (NLM, AMA, PlanOverride, etc.)</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// A coding (system + code + display) in a translation result.
/// </summary>
public class TranslatedCoding
{
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Display { get; set; } = string.Empty;
}

/// <summary>
/// Request model for a lightweight CodeSystem/$lookup display lookup.
/// </summary>
public class CodeLookupRequest
{
    /// <summary>FHIR coding system URI</summary>
    public string System { get; set; } = string.Empty;

    /// <summary>Code to look up</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Optional tenant ID for plan-specific terminology overrides</summary>
    public string? TenantId { get; set; }
}

/// <summary>
/// Response model for a lightweight code display lookup.
/// </summary>
public class CodeLookupResponse
{
    public bool Result { get; set; }
    public string System { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Display { get; set; }
    public string? Message { get; set; }
    public string? MapVersionId { get; set; }
    public string? Source { get; set; }
    public DateTime LookedUpAt { get; set; } = DateTime.UtcNow;
}
