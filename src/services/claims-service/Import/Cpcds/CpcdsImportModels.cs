using ClaimsService.Models;

namespace ClaimsService.Import.Cpcds;

/// <summary>How one CPCDS claim file is to be loaded.</summary>
public sealed class CpcdsImportOptions
{
    /// <summary>The system that adjudicated the claims (e.g. <c>qnxt</c>, <c>facets</c>, <c>healthrules</c>). Recorded on each claim.</summary>
    public string? SourceSystem { get; init; }

    /// <summary>The file name, for the report and the claim's provenance.</summary>
    public string? FileName { get; init; }

    /// <summary>When the source system produced the extract. Freshness is measured from this.</summary>
    public DateTime? ExtractedAt { get; init; }

    /// <summary>The line of business the claims belong to. CPCDS has no element for it, so the file carries one.</summary>
    public LineOfBusiness LineOfBusiness { get; init; } = LineOfBusiness.Commercial;

    /// <summary>
    /// The file's own header for a CPCDS column, keyed by the CPCDS name (or
    /// the <see cref="CpcdsClaimColumn"/> name). Only the columns that differ
    /// need an entry.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ColumnMap { get; init; }

    /// <summary>Parse and map, report what would happen, write nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>One claim as the file describes it: its rows, grouped.</summary>
internal sealed class CpcdsClaimRecord
{
    public required string ClaimId { get; init; }

    /// <summary>Every row of the claim, in file order, with the line each started on.</summary>
    public List<CpcdsRow> Rows { get; } = [];
}

/// <summary>One data row: its line in the file and its values by column.</summary>
internal sealed record CpcdsRow(int LineNumber, IReadOnlyDictionary<CpcdsClaimColumn, string> Values)
{
    public string? Get(CpcdsClaimColumn column)
        => Values.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>What happened to one claim.</summary>
public enum CpcdsClaimOutcome
{
    /// <summary>New to CHO; version 1 written.</summary>
    Created,

    /// <summary>Already loaded, and the source changed; a new version was written and the old one superseded.</summary>
    Updated,

    /// <summary>Already loaded with identical content; nothing written.</summary>
    Unchanged,

    /// <summary>Not loaded; see the errors.</summary>
    Rejected,
}

/// <summary>The import's answer for one claim.</summary>
public sealed class CpcdsClaimResult
{
    public string ClaimId { get; init; } = string.Empty;
    public CpcdsClaimOutcome Outcome { get; set; }

    /// <summary>The CHO claim (and FHIR ExplanationOfBenefit) id, when loaded.</summary>
    public string? ChoClaimId { get; set; }
    public int? VersionNumber { get; set; }
    public string? MemberId { get; set; }
    public List<int> FileLines { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// The import report: counts and money in the file against what was loaded,
/// so whoever dropped the file can reconcile it against the core's own
/// control totals without opening a claim.
/// </summary>
public sealed class CpcdsImportResult
{
    public string BatchId { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string? SourceSystem { get; init; }
    public bool DryRun { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; set; }
    public DateTime? ExtractedAt { get; init; }

    /// <summary>The delimiter detected from the header line.</summary>
    public string Delimiter { get; set; } = ",";

    public int RowsRead { get; set; }
    public int ClaimsInFile { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Rejected { get; set; }

    /// <summary>Sum of "Claim total submitted amount" over every claim in the file.</summary>
    public decimal SubmittedAmountInFile { get; set; }

    /// <summary>Sum of "Claim payment amount" over every claim in the file.</summary>
    public decimal PaidAmountInFile { get; set; }

    /// <summary>Sum of the payment amount over the claims now current in CHO from this file (created, updated or unchanged).</summary>
    public decimal PaidAmountLoaded { get; set; }

    /// <summary>Headers the importer did not recognize and ignored.</summary>
    public List<string> UnrecognizedColumns { get; set; } = [];

    /// <summary>File-level problems (a missing required column, an empty file). When present, no claim was loaded.</summary>
    public List<string> FileErrors { get; set; } = [];

    public List<CpcdsClaimResult> Claims { get; set; } = [];
}
