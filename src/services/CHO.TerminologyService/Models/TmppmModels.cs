using MongoDB.Bson.Serialization.Attributes;

namespace CHO.TerminologyService.Models;

// TMPPM (Texas Medicaid Provider Procedures Manual) prior-authorization rules,
// editions and edition diffs. These are published state Medicaid policy, shared
// by every tenant (global data, like the NLM maps): reads need terminology:read,
// writes platform:admin. A tenant's use of them (ConceptMap overrides that make
// $translate answer "auth required") is tenant data: settings:manage, the token
// tenant. Stored by terminology-service only (collections tmppm_pa_rules,
// tmppm_editions, tmppm_diff_reports); the portal and the ingestion tool use the
// API. Field names are the driver defaults the ingestion tool always wrote.

/// <summary>One extracted PA rule (unique by <see cref="RuleId"/>).</summary>
[BsonIgnoreExtraElements]
public class TmppmPaRule
{
    public string RuleId { get; set; } = string.Empty;
    public string State { get; set; } = "TX";
    public string Category { get; set; } = string.Empty;
    public string TmppmRef { get; set; } = string.Empty;
    /// <summary>AuthRequired, DiagnosisRestriction, AgeLimit, UnitLimit, Noncovered.</summary>
    public string RuleType { get; set; } = string.Empty;
    public List<string> ProcedureCodes { get; set; } = [];
    /// <summary>CPT or HCPCS.</summary>
    public string CodeSystem { get; set; } = "CPT";
    public bool AuthRequired { get; set; }
    public string? AuthType { get; set; }
    public List<string>? AllowedDiagnoses { get; set; }
    public List<string>? ExcludedDiagnoses { get; set; }
    public TmppmAgeRule? AgeLimit { get; set; }
    public TmppmUnitLimitRule? UnitLimit { get; set; }
    public List<string>? RequiredModifiers { get; set; }
    public List<string>? RequiredDocumentation { get; set; }
    public string? ClinicalCriteriaSummary { get; set; }
    public DateOnly EffectiveDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public string SourceEdition { get; set; } = string.Empty;
    public string? Notes { get; set; }
}

[BsonIgnoreExtraElements]
public class TmppmAgeRule
{
    public int? MinAge { get; set; }
    public int? MaxAge { get; set; }
    public string? Unit { get; set; } = "years";
}

[BsonIgnoreExtraElements]
public class TmppmUnitLimitRule
{
    public int MaxUnits { get; set; }
    public string Per { get; set; } = string.Empty;
    public string? ResetCondition { get; set; }
}

/// <summary>A TMPPM edition (unique by <see cref="EditionId"/>, e.g. "2026-04").</summary>
[BsonIgnoreExtraElements]
public class TmppmEdition
{
    public string EditionId { get; set; } = string.Empty;
    public DateOnly PublicationDate { get; set; }
    public DateOnly PolicyThroughDate { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string? ReleaseNotesUrl { get; set; }
    public DateTime IngestedAt { get; set; }
    public List<TmppmChapter> Chapters { get; set; } = [];
}

[BsonIgnoreExtraElements]
public class TmppmChapter
{
    public string ChapterId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PdfFileName { get; set; } = string.Empty;
    public string PdfUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public int ExtractedRuleCount { get; set; }
}

/// <summary>Differences between two editions.</summary>
[BsonIgnoreExtraElements]
public class TmppmDiffReport
{
    public string FromEdition { get; set; } = string.Empty;
    public string ToEdition { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public List<TmppmRuleDelta> Deltas { get; set; } = [];
}

[BsonIgnoreExtraElements]
public class TmppmRuleDelta
{
    /// <summary>Added, Modified, Removed.</summary>
    public string DeltaType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? Description { get; set; }
    public bool RequiresHumanReview { get; set; }
}

/// <summary>Rule categories of a state, grouped by priority (P1 auth/diagnosis, P2 age/units, P3 the rest).</summary>
public class TmppmCategoryGroup
{
    public string Priority { get; set; } = string.Empty;
    public List<TmppmCategorySummary> Categories { get; set; } = [];
}

public class TmppmCategorySummary
{
    public string Category { get; set; } = string.Empty;
    public string TmppmRef { get; set; } = string.Empty;
    public int RuleCount { get; set; }
    public int CodeCount { get; set; }
}

/// <summary>Body of <c>POST /api/v1/tmppm/overrides</c>.</summary>
public class TmppmPublishOverridesRequest
{
    /// <summary>The edition the rules come from (names the override map version).</summary>
    public string EditionId { get; set; } = string.Empty;

    public List<TmppmPaRule> Rules { get; set; } = [];
}

public class TmppmPublishOverridesResult
{
    public string MapVersionId { get; set; } = string.Empty;
    public int OverridesPublished { get; set; }
}
