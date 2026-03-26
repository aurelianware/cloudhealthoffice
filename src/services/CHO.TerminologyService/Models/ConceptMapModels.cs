namespace CHO.TerminologyService.Models;

/// <summary>
/// Internal representation of a concept mapping entry.
/// Stored in MongoDB, sourced from NLM RF2, AMA cross maps, or FHIR ConceptMap imports.
/// </summary>
public class ConceptMapEntry
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Source coding system URI (e.g., http://snomed.info/sct)</summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>Source code (e.g., SNOMED concept ID "390840006")</summary>
    public string SourceCode { get; set; } = string.Empty;

    /// <summary>Human-readable display for source code</summary>
    public string SourceDisplay { get; set; } = string.Empty;

    /// <summary>Target coding system URI (e.g., http://hl7.org/fhir/sid/icd-10-cm)</summary>
    public string TargetSystem { get; set; } = string.Empty;

    /// <summary>Target code (e.g., ICD-10-CM "Z23")</summary>
    public string TargetCode { get; set; } = string.Empty;

    /// <summary>Human-readable display for target code</summary>
    public string TargetDisplay { get; set; } = string.Empty;

    /// <summary>FHIR equivalence: equivalent, wider, narrower, inexact, unmatched, disjoint</summary>
    public string Equivalence { get; set; } = "equivalent";

    /// <summary>Map group identifier (for grouping related entries)</summary>
    public string MapGroupId { get; set; } = string.Empty;

    /// <summary>Priority when multiple targets exist (lower = preferred)</summary>
    public int Priority { get; set; } = 1;

    /// <summary>Map rule for context-dependent translations (age, gender, co-morbidity)</summary>
    public MapRule? Rule { get; set; }

    /// <summary>Which map version this entry belongs to</summary>
    public string MapVersionId { get; set; } = string.Empty;

    /// <summary>Whether this is a plan-specific override</summary>
    public bool IsOverride { get; set; } = false;

    /// <summary>Tenant ID for plan-specific overrides (null = global)</summary>
    public string? TenantId { get; set; }
}

/// <summary>
/// Contextual rule for disambiguating one-to-many SNOMED→ICD mappings.
/// NLM maps include age/gender rules; plans add their own (TMPPM, state Medicaid, etc.).
/// </summary>
public class MapRule
{
    /// <summary>Rule type: Age, Gender, CoMorbidity, StateSpecific, Custom</summary>
    public string RuleType { get; set; } = string.Empty;

    /// <summary>For age rules: minimum age in years (inclusive)</summary>
    public int? AgeMin { get; set; }

    /// <summary>For age rules: maximum age in years (inclusive)</summary>
    public int? AgeMax { get; set; }

    /// <summary>For gender rules: male, female, other</summary>
    public string? Gender { get; set; }

    /// <summary>For co-morbidity rules: required co-occurring SNOMED codes</summary>
    public List<string>? CoMorbidCodes { get; set; }

    /// <summary>For state-specific rules: state code (e.g., "TX" for Texas/TMPPM)</summary>
    public string? StateCode { get; set; }

    /// <summary>Free-form expression for complex rules (FHIRPath or custom DSL)</summary>
    public string? Expression { get; set; }
}

/// <summary>
/// Tracks which version of each crosswalk is loaded.
/// Enables audit trail: "which map was active when this translation happened?"
/// </summary>
public class MapVersion
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Map identifier (e.g., "NLM-SNOMED-ICD10CM", "AMA-CPT-SNOMED")</summary>
    public string MapName { get; set; } = string.Empty;

    /// <summary>Version string from the source (e.g., "202603" for March 2026 US Edition)</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Source system URI</summary>
    public string SourceSystem { get; set; } = string.Empty;

    /// <summary>Target system URI</summary>
    public string TargetSystem { get; set; } = string.Empty;

    /// <summary>When this version was imported</summary>
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether this version is the active/current one</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Total number of entries in this version</summary>
    public int EntryCount { get; set; }

    /// <summary>Checksum of source file for deduplication</summary>
    public string? SourceChecksum { get; set; }
}
