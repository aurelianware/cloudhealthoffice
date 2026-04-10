namespace CHO.TmppmIngestionService.Models;

/// <summary>
/// A single extracted PA rule from TMPPM. Transforms into ConceptMapEntry overrides
/// for the TerminologyService and PriorAuthService rule stores.
/// </summary>
public class TmppmPaRule
{
    public string RuleId { get; set; } = string.Empty;
    public string State { get; set; } = "TX";
    public string Category { get; set; } = string.Empty;
    public string TmppmRef { get; set; } = string.Empty;
    public string RuleType { get; set; } = string.Empty; // AuthRequired, DiagnosisRestriction, AgeLimit, UnitLimit, Noncovered
    public List<string> ProcedureCodes { get; set; } = [];
    public string CodeSystem { get; set; } = "CPT"; // CPT, HCPCS
    public bool AuthRequired { get; set; }
    public string? AuthType { get; set; } // SMPA, Online, Fax, Phone
    public List<string>? AllowedDiagnoses { get; set; }
    public List<string>? ExcludedDiagnoses { get; set; }
    public AgeRule? AgeLimit { get; set; }
    public UnitLimitRule? UnitLimit { get; set; }
    public List<string>? RequiredModifiers { get; set; }
    public List<string>? RequiredDocumentation { get; set; }
    public string? ClinicalCriteriaSummary { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string SourceEdition { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

public class AgeRule
{
    public int? MinAge { get; set; }
    public int? MaxAge { get; set; }
    public string? Unit { get; set; } = "years"; // years, months, days
}

public class UnitLimitRule
{
    public int MaxUnits { get; set; }
    public string Per { get; set; } = string.Empty; // day, visit, auth_period, calendar_year, lifetime
    public string? ResetCondition { get; set; }
}

/// <summary>
/// Represents a TMPPM edition with metadata for version tracking and monthly diff.
/// </summary>
public class TmppmEdition
{
    public string EditionId { get; set; } = string.Empty; // e.g., "2026-04"
    public DateOnly PublicationDate { get; set; }
    public DateOnly PolicyThroughDate { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string? ReleaseNotesUrl { get; set; }
    public DateTime IngestedAt { get; set; }
    public List<TmppmChapter> Chapters { get; set; } = [];
}

public class TmppmChapter
{
    public string ChapterId { get; set; } = string.Empty; // e.g., "2_13_med_specs_and_phys_srvs"
    public string Title { get; set; } = string.Empty;
    public string PdfFileName { get; set; } = string.Empty;
    public string PdfUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public int ExtractedRuleCount { get; set; }
}

/// <summary>
/// Monthly diff between two TMPPM editions.
/// </summary>
public class TmppmDiffReport
{
    public string FromEdition { get; set; } = string.Empty;
    public string ToEdition { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public List<TmppmRuleDelta> Deltas { get; set; } = [];
    public int AddedCount => Deltas.Count(d => d.DeltaType == "Added");
    public int ModifiedCount => Deltas.Count(d => d.DeltaType == "Modified");
    public int RemovedCount => Deltas.Count(d => d.DeltaType == "Removed");
}

public class TmppmRuleDelta
{
    public string DeltaType { get; set; } = string.Empty; // Added, Modified, Removed
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? Description { get; set; }
    public bool RequiresHumanReview { get; set; }
}

/// <summary>
/// Maps to the existing CHO TerminologyService ConceptMapEntry for MongoDB persistence.
/// The ingestion pipeline outputs these for bulk upsert into the terminology-service collection.
/// </summary>
public class ConceptMapEntryOverride
{
    public string Id { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public string SourceDisplay { get; set; } = string.Empty;
    public string TargetSystem { get; set; } = string.Empty;
    public string TargetCode { get; set; } = string.Empty;
    public string TargetDisplay { get; set; } = string.Empty;
    public string Equivalence { get; set; } = "equivalent";
    public string MapGroupId { get; set; } = string.Empty;
    public int Priority { get; set; } = 1;
    public MapRule? Rule { get; set; }
    public string MapVersionId { get; set; } = string.Empty;
    public bool IsOverride { get; set; } = true;
    public string? TenantId { get; set; }
}

public class MapRule
{
    public string RuleType { get; set; } = "StateSpecific";
    public int? AgeMin { get; set; }
    public int? AgeMax { get; set; }
    public string? Gender { get; set; }
    public List<string>? CoMorbidCodes { get; set; }
    public string? State { get; set; }
}
