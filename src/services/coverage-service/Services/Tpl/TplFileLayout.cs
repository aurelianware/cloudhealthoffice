using System.Text.Json.Serialization;
using CoverageService.Models.Tpl;

namespace CoverageService.Services.Tpl;

/// <summary>
/// How to read one state's TPL file: delimited with a header (columns found by
/// alias) or fixed width (start and length per field), plus the table that maps
/// the state's coverage codes to <see cref="TplScope"/>.
///
/// Built-in layouts live in <see cref="TplLayouts"/>. A plan whose state file
/// differs uploads its own layout as JSON with the file; the shape is this
/// class, serialised with camelCase names.
/// </summary>
public sealed class TplFileLayout
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Two-letter state the file comes from. Empty means the caller
    /// supplies it (the generic CSV layout).</summary>
    public string State { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public TplFileFormat Format { get; set; } = TplFileFormat.Delimited;

    /// <summary>Field delimiter for <see cref="TplFileFormat.Delimited"/>.
    /// Tab is written as <c>"\t"</c>.</summary>
    public string Delimiter { get; set; } = ",";

    /// <summary>Header aliases (delimited) or positions (fixed width) per
    /// field. Keys are <see cref="TplField"/> names.</summary>
    public Dictionary<TplField, TplFieldSpec> Fields { get; set; } = new();

    /// <summary>State coverage code to scope. Codes are matched
    /// case-insensitively after trimming. A code not in the table falls back
    /// to scope names (<c>Comprehensive</c>, <c>Dental</c>, <c>Medicare Part
    /// B</c>…); a code neither resolves is reported and not loaded.</summary>
    public Dictionary<string, TplCodeMapping> Codes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Date formats tried in order. Defaults cover ISO, US slashes
    /// and the CCYYMMDD most state files use.</summary>
    public List<string> DateFormats { get; set; } = ["yyyyMMdd", "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "MMddyyyy"];

    /// <summary>Values that mean "no end date" (open span).</summary>
    public List<string> OpenEndDates { get; set; } = ["", "99991231", "9999-12-31", "12/31/9999", "00000000"];

    /// <summary>False until the layout has been checked against the state's
    /// published record layout. The import report repeats this so nobody
    /// loads production data on a guess.</summary>
    public bool Verified { get; set; }

    /// <summary>What has and has not been checked, and against which
    /// document.</summary>
    public string VerificationNote { get; set; } = string.Empty;

    /// <summary>Sources for the code table and rules.</summary>
    public List<string> References { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TplFileFormat
{
    Delimited,
    FixedWidth,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TplField
{
    MemberId,
    CarrierCode,
    CarrierName,
    PolicyNumber,
    GroupNumber,
    PolicyholderName,
    /// <summary>The state's coverage code (Medi-Cal OHC code, Texas coverage type).</summary>
    CoverageCode,
    /// <summary>Optional explicit scope names, e.g. <c>Hospital;Professional</c>.
    /// Used when there is no coverage code or alongside it.</summary>
    Scope,
    EffectiveDate,
    EndDate,
    /// <summary>Optional add / change / end / remove indicator.</summary>
    RecordAction,
}

public sealed class TplFieldSpec
{
    /// <summary>Header names that identify the column (delimited files).</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>1-based start column (fixed-width files).</summary>
    public int Start { get; set; }

    /// <summary>Width in characters (fixed-width files).</summary>
    public int Length { get; set; }

    public static TplFieldSpec Alias(params string[] aliases) => new() { Aliases = aliases.ToList() };
}

public sealed class TplCodeMapping
{
    public TplScope Scope { get; set; }

    /// <summary>Medicaid pays first and recovers for every service.</summary>
    public bool PayAndChaseOnly { get; set; }

    /// <summary>The code says the member has no TPL (Medi-Cal OHC <c>N</c>):
    /// every active row this source holds for the member is ended.</summary>
    public bool MeansNoCoverage { get; set; }

    /// <summary>The code is a flag, not a coverage source (Medi-Cal
    /// <c>G</c>, <c>I</c>); the row is skipped and counted.</summary>
    public bool Skip { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>The mapping to a scope is CHO's reading, not the state's
    /// published crosswalk; the import report lists such codes.</summary>
    public bool ScopeNeedsVerification { get; set; }
}
