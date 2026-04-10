namespace CloudHealthOffice.Portal.Models;

public class TmppmPaRuleViewModel
{
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string TmppmRef { get; set; } = string.Empty;
    public bool AuthRequired { get; set; }
    public string? AuthType { get; set; }
    public List<string> ProcedureCodes { get; set; } = [];
    public string CodeSystem { get; set; } = "CPT";
    public string? ClinicalCriteriaSummary { get; set; }
    public List<string>? RequiredDocumentation { get; set; }
    public AgeRuleViewModel? AgeLimit { get; set; }
    public UnitLimitViewModel? UnitLimit { get; set; }
    public List<string>? AllowedDiagnoses { get; set; }
    public string State { get; set; } = "TX";
    public string SourceEdition { get; set; } = string.Empty;
}

public class AgeRuleViewModel
{
    public int? MinAge { get; set; }
    public int? MaxAge { get; set; }
    public string? Unit { get; set; } = "years";
}

public class UnitLimitViewModel
{
    public int MaxUnits { get; set; }
    public string Per { get; set; } = string.Empty;
    public string? ResetCondition { get; set; }
}

public class PaCategoryGroup
{
    public string Priority { get; set; } = string.Empty;
    public List<PaCategorySummary> Categories { get; set; } = [];
}

public class PaCategorySummary
{
    public string Category { get; set; } = string.Empty;
    public string TmppmRef { get; set; } = string.Empty;
    public int RuleCount { get; set; }
    public int CodeCount { get; set; }
}

public class TmppmEditionViewModel
{
    public string EditionId { get; set; } = string.Empty;
    public DateOnly PublicationDate { get; set; }
    public DateOnly PolicyThroughDate { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime IngestedAt { get; set; }
    public List<TmppmChapterViewModel> Chapters { get; set; } = [];
}

public class TmppmChapterViewModel
{
    public string ChapterId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string PdfUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public int ExtractedRuleCount { get; set; }
}

public class TmppmDiffViewModel
{
    public string FromEdition { get; set; } = string.Empty;
    public string ToEdition { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public List<TmppmRuleDeltaViewModel> Deltas { get; set; } = [];
    public int AddedCount => Deltas.Count(d => d.DeltaType == "Added");
    public int ModifiedCount => Deltas.Count(d => d.DeltaType == "Modified");
    public int RemovedCount => Deltas.Count(d => d.DeltaType == "Removed");
}

public class TmppmRuleDeltaViewModel
{
    public string DeltaType { get; set; } = string.Empty;
    public string RuleId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? PreviousValue { get; set; }
    public string? NewValue { get; set; }
    public string? Description { get; set; }
    public bool RequiresHumanReview { get; set; }
}
