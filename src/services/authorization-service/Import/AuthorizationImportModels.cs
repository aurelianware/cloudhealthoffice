using System.Text.Json.Serialization;
using AuthorizationService.Models;

namespace AuthorizationService.Import;

/// <summary>How one authorization file is to be loaded.</summary>
public sealed class AuthorizationImportOptions
{
    /// <summary>The system that decided the authorizations (e.g. <c>jiva</c>, <c>guidingcare</c>, <c>qnxt</c>). Recorded on each.</summary>
    public string? SourceSystem { get; init; }

    /// <summary>The file name, for the report and each authorization's provenance.</summary>
    public string? FileName { get; init; }

    /// <summary>When the source produced the extract.</summary>
    public DateTime? ExtractedAt { get; init; }

    /// <summary>The line of business for rows that do not name one.</summary>
    public LineOfBusiness LineOfBusiness { get; init; } = LineOfBusiness.Commercial;

    /// <summary>The file's own header for a column, keyed by the documented header (only the ones that differ).</summary>
    public IReadOnlyDictionary<string, string>? ColumnMap { get; init; }

    /// <summary>Parse and map, report what would happen, write nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>What happened to one authorization.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuthorizationImportOutcome
{
    /// <summary>New to CHO.</summary>
    Created,

    /// <summary>Already loaded, and the source changed (a decision, an extension, a correction).</summary>
    Updated,

    /// <summary>Already loaded with identical content; nothing written.</summary>
    Unchanged,

    /// <summary>Not loaded; see the errors.</summary>
    Rejected,
}

/// <summary>The import's answer for one authorization.</summary>
public sealed class AuthorizationImportItem
{
    public string AuthorizationNumber { get; init; } = string.Empty;
    public AuthorizationImportOutcome Outcome { get; set; }

    /// <summary>The CHO authorization id, when loaded.</summary>
    public string? ChoAuthorizationId { get; set; }
    public string? Status { get; set; }
    public string? MemberId { get; set; }
    public List<int> FileLines { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>The import report.</summary>
public sealed class AuthorizationImportResult
{
    public string BatchId { get; init; } = string.Empty;
    public string Format { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string? SourceSystem { get; init; }
    public bool DryRun { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExtractedAt { get; init; }
    /// <summary>CSV: the delimiter the header used.</summary>
    public string? Delimiter { get; set; }

    /// <summary>CSV: data rows read.</summary>
    public int RowsRead { get; set; }

    /// <summary>X12: 278 transaction sets read (requests and responses).</summary>
    public int TransactionSets { get; set; }
    public int AuthorizationsInFile { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Rejected { get; set; }
    public List<string> FileErrors { get; } = [];
    public List<string> UnrecognizedColumns { get; set; } = [];
    public List<AuthorizationImportItem> Authorizations { get; } = [];
}

/// <summary>
/// One authorization as a source file describes it, mapped to CHO's model:
/// what <see cref="AuthorizationImportService"/> stores. Every format (CSV,
/// X12 278) produces these.
/// </summary>
public sealed class AuthorizationImportCandidate
{
    public required string AuthorizationNumber { get; init; }

    /// <summary>The mapped authorization, or null when the source rows could not be mapped (see <see cref="Errors"/>).</summary>
    public Authorization? Authorization { get; init; }

    /// <summary>SHA-256 of the source content that produced it.</summary>
    public required string ContentHash { get; init; }

    public string? SourceMemberId { get; init; }
    public List<int> FileLines { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
