using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Services;

namespace BenefitPlanService.Import.PriorAuthRules;

/// <summary>The file could not be read as a prior-auth-required list.</summary>
public sealed class PriorAuthRuleFormatException(string message) : Exception(message);

/// <summary>Where the imported list applies and how it is written.</summary>
public sealed record PriorAuthRuleImportOptions
{
    /// <summary>Two-letter state, upper case.</summary>
    public required string StateCode { get; init; }
    public required PaLineOfBusiness LineOfBusiness { get; init; }
    /// <summary>Program code (e.g. STAR); null = every program of the state and line of business.</summary>
    public string? Program { get; init; }
    /// <summary>File name, kept on each rule's description as provenance.</summary>
    public string? SourceName { get; init; }
    /// <summary>
    /// Copy the exemption rules (gold card, provider type, member age) of the rule
    /// set this import replaces into the imported set, so a plan's own list does
    /// not silently switch off a state's gold-card law.
    /// </summary>
    public bool KeepExemptions { get; init; } = true;
    public bool DryRun { get; init; } = true;
    /// <summary>Return every code of every rule, not the first 25.</summary>
    public bool IncludeCodes { get; init; }
}

public enum PriorAuthRuleImportOutcome
{
    /// <summary>Dry run: this is what would be written.</summary>
    WouldWrite,
    Written,
    /// <summary>No row produced a rule; nothing was or would be written.</summary>
    NothingToImport,
}

/// <summary>One rule the import writes (or would write).</summary>
public sealed record ImportedPriorAuthRule
{
    public required string RuleId { get; init; }
    public required string Name { get; init; }
    /// <summary>The engine rule type: ProcedureRequiresAuth, DiagnosisRequired or QuantityLimit.</summary>
    public required string RuleType { get; init; }
    public required PaRequirementCondition Condition { get; init; }
    public int CodeCount { get; init; }
    public IReadOnlyList<string> Codes { get; init; } = [];
    public IReadOnlyList<string> CodePrefixes { get; init; } = [];
    public IReadOnlyList<string> PlacesOfService { get; init; } = [];
    public IReadOnlyList<string> Diagnoses { get; init; } = [];
    public int? UnitLimit { get; init; }
    public int? VisitLimit { get; init; }
    public bool DeniedWithoutReview { get; init; }
    public bool AllServices { get; init; }
}

/// <summary>A row that was not loaded, and why.</summary>
public sealed record PriorAuthRuleSkippedRow(int Row, string Reason, string? Value);

public sealed record PriorAuthRuleImportReport
{
    public required PriorAuthRuleImportOutcome Outcome { get; init; }
    /// <summary>The tenant rule set written, e.g. <c>tenant-a/TX/Medicaid/STAR</c>.</summary>
    public required string RuleSet { get; init; }
    public int HeaderRow { get; init; }
    /// <summary>Which file column was read for each role.</summary>
    public IReadOnlyDictionary<string, string> Columns { get; init; } = new Dictionary<string, string>();
    public int RowsRead { get; init; }
    public int RowsLoaded { get; init; }
    /// <summary>Rows not loaded, counted by reason.</summary>
    public IReadOnlyDictionary<string, int> Skipped { get; init; } = new Dictionary<string, int>();
    /// <summary>The first skipped rows, for fixing the file.</summary>
    public IReadOnlyList<PriorAuthRuleSkippedRow> SkippedExamples { get; init; } = [];
    public IReadOnlyList<ImportedPriorAuthRule> Rules { get; init; } = [];
    /// <summary>Distinct procedure codes the imported rules name (ranges expanded; prefixes not counted).</summary>
    public int DistinctCodes { get; init; }
    /// <summary>HCPCS J codes in the file: the engine applies them, the CMS-0057-F public list leaves drugs out.</summary>
    public IReadOnlyList<string> DrugCodes { get; init; } = [];
    /// <summary>The rule set requests for this scope resolved to before the import (null when none).</summary>
    public string? ReplacesRuleSet { get; init; }
    /// <summary>Rules of that set that stop applying once the import is in place.</summary>
    public IReadOnlyList<string> RulesNoLongerApplied { get; init; } = [];
    /// <summary>Exemption rules copied into the imported set (<see cref="PriorAuthRuleImportOptions.KeepExemptions"/>).</summary>
    public IReadOnlyList<string> ExemptionsKept { get; init; } = [];
    /// <summary>Rules of an earlier import of this scope that this file no longer has; disabled on write.</summary>
    public IReadOnlyList<string> RulesDisabled { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
