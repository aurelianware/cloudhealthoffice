using System.Text.Json.Serialization;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;

namespace BenefitPlanService.Import.FeeSchedules;

/// <summary>What to do when the file holds two rates for the same code and modifier.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DuplicateRatePolicy
{
    /// <summary>Report the conflicts and write nothing (the default): a filter is usually missing.</summary>
    Reject,
    /// <summary>Keep the first row in file order.</summary>
    First,
    /// <summary>Keep the lowest rate.</summary>
    Lowest,
    /// <summary>Keep the highest rate.</summary>
    Highest,
}

/// <summary>Options for one fee schedule CSV import.</summary>
public sealed record FeeScheduleImportOptions
{
    /// <summary>Lines one schedule document may hold (a MongoDB document stays well under 16 MB).</summary>
    public const int MaxLines = 50_000;

    public required string Name { get; init; }
    public required DateTime EffectiveDate { get; init; }
    public DateTime? TermDate { get; init; }

    /// <summary>Medicaid, Commercial or Custom (all priced as flat rates per unit).</summary>
    public FeeScheduleType Type { get; init; } = FeeScheduleType.Custom;

    /// <summary>Provenance; defaults to PublicGovernment for Medicaid, PayerContract otherwise.</summary>
    public FeeScheduleSourceType? SourceType { get; init; }

    /// <summary>Short source label, e.g. "tx-tmhp-physician" or "ca-medi-cal-rates".</summary>
    public string? SourceId { get; init; }

    /// <summary>Publisher's version or posting date, e.g. "2026-10-01".</summary>
    public string? SourceVersion { get; init; }

    /// <summary>Two-letter state for a state schedule.</summary>
    public string? Jurisdiction { get; init; }

    /// <summary>
    /// When set, the schedule becomes this benefit plan's default (one per plan;
    /// see PlanDefaultFeeScheduleController): a plan code for every version of
    /// the plan, or a plan version id for that version only (<see cref="PlanPricingKey"/>).
    /// </summary>
    public string? PlanId { get; init; }

    /// <summary>Keep only rows whose column equals the value (case-insensitive), e.g. "Provider Type" = "Physician".</summary>
    public IReadOnlyList<(string Column, string Value)> Filters { get; init; } = [];

    public FeeScheduleColumnOverrides Columns { get; init; } = new();
    public DuplicateRatePolicy OnDuplicate { get; init; } = DuplicateRatePolicy.Reject;

    /// <summary>Load $0.00 rows. Off by default: a state's $0 usually means manually priced or not payable, and a missing line pends.</summary>
    public bool KeepZeroRates { get; init; }

    /// <summary>Replace an existing schedule with the same id. Off by default.</summary>
    public bool Replace { get; init; }

    /// <summary>True (the default) parses and reports without writing.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>Return every mapped line, not only a sample.</summary>
    public bool IncludeLines { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FeeScheduleImportOutcome
{
    /// <summary>Dry run: would be written.</summary>
    WouldWrite,
    Created,
    Replaced,
    /// <summary>The stored schedule already has these lines (same checksum); nothing written.</summary>
    Unchanged,
    /// <summary>A schedule with this id exists and replace was not set.</summary>
    Exists,
    /// <summary>Conflicting duplicate rates, too many lines, or no lines; nothing written.</summary>
    Rejected,
}

/// <summary>A row the importer did not load, and why.</summary>
public sealed record FeeScheduleSkippedRow(
    [property: JsonPropertyName("row")] int Row,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("reason")] string Reason);

/// <summary>Two or more rows with different rates for one code and modifier.</summary>
public sealed record FeeScheduleDuplicate(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("modifier")] string? Modifier,
    [property: JsonPropertyName("rows")] IReadOnlyList<int> Rows,
    [property: JsonPropertyName("rates")] IReadOnlyList<decimal> Rates);

/// <summary>Result of a fee schedule import (dry run or real).</summary>
public sealed class FeeScheduleImportReport
{
    /// <summary>Examples kept per skip reason.</summary>
    public const int ExamplesPerReason = 5;

    public bool DryRun { get; init; }
    public FeeScheduleImportOutcome Outcome { get; set; }
    public string ScheduleId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public FeeScheduleType Type { get; init; }
    public string? DefaultForPlanId { get; init; }

    /// <summary>Which file column was read for each role (so a wrong guess is visible).</summary>
    public IReadOnlyDictionary<string, string> Columns { get; set; } = new Dictionary<string, string>();
    public int HeaderRow { get; set; }

    public int RowsRead { get; set; }
    public int RowsFilteredOut { get; set; }
    public int LinesLoaded { get; set; }
    public int LinesWithFacilityRate { get; set; }

    /// <summary>Skipped row counts by reason.</summary>
    public Dictionary<string, int> Skipped { get; } = new();
    public List<FeeScheduleSkippedRow> SkippedExamples { get; } = new();

    /// <summary>Codes with conflicting rates (reported even when a policy resolved them).</summary>
    public int DuplicateKeys { get; set; }
    public List<FeeScheduleDuplicate> DuplicateExamples { get; } = new();

    public string Checksum { get; set; } = string.Empty;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>The first lines, or every line when includeLines was set.</summary>
    public List<FeeScheduleLine> Lines { get; set; } = new();
}
