using System.Text.RegularExpressions;
using ClaimsService.Models;

namespace ClaimsService.Import.Cpcds.Drop;

/// <summary>
/// A tenant's drop settings as the API takes and returns them. Durations are
/// whole hours so a setup form can offer them directly.
/// </summary>
public sealed class CpcdsDropSettingsRequest
{
    public const int MaxColumnMapEntries = 200;
    public const int MaxColumnNameLength = 200;
    public const int MaxExpectedEveryHours = 24 * 31;
    public const int MaxStaleGraceHours = 24 * 7;

    private static readonly Regex SourceSystemPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>Load this tenant's drop. False keeps the settings and stops loading.</summary>
    public bool Enabled { get; set; }

    /// <summary>The core the extracts come from, e.g. <c>qnxt</c>, <c>facets</c>; recorded on each claim.</summary>
    public string? SourceSystem { get; set; }

    /// <summary>Commercial, Medicare, Medicaid, Exchange, TRICARE or VA (a name; a number is also accepted).</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public LineOfBusiness LineOfBusiness { get; set; } = LineOfBusiness.Commercial;

    /// <summary>CPCDS column name to the extract's header, for extracts that name columns differently.</summary>
    public Dictionary<string, string>? ColumnMap { get; set; }

    /// <summary>Shadow-adjudicate each claim file after it loads (when shadow adjudication is on for the tenant).</summary>
    public bool ShadowAfterImport { get; set; }

    /// <summary>How often the core sends a claim extract, in hours.</summary>
    public int ExpectedEveryHours { get; set; } = 24;

    /// <summary>How late an extract may be, in hours, before the drop is reported stale.</summary>
    public int StaleGraceHours { get; set; } = 6;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (SourceSystem is not null && !SourceSystemPattern.IsMatch(SourceSystem))
            errors.Add("sourceSystem: letters, digits, '.', '_' and '-', at most 64 characters.");
        if (!Enum.IsDefined(LineOfBusiness))
            errors.Add("lineOfBusiness: not a known line of business.");
        if (ExpectedEveryHours < 1 || ExpectedEveryHours > MaxExpectedEveryHours)
            errors.Add($"expectedEveryHours: between 1 and {MaxExpectedEveryHours}.");
        if (StaleGraceHours < 0 || StaleGraceHours > MaxStaleGraceHours)
            errors.Add($"staleGraceHours: between 0 and {MaxStaleGraceHours}.");
        if (ColumnMap is { } map)
        {
            if (map.Count > MaxColumnMapEntries)
                errors.Add($"columnMap: at most {MaxColumnMapEntries} entries.");
            if (map.Any(kv => string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)
                              || kv.Key.Length > MaxColumnNameLength || kv.Value.Length > MaxColumnNameLength))
                errors.Add($"columnMap: names and headers must be non-empty and at most {MaxColumnNameLength} characters.");
        }
        return errors;
    }

    public CpcdsDropTenantOptions ToOptions() => new()
    {
        SourceSystem = string.IsNullOrWhiteSpace(SourceSystem) ? null : SourceSystem,
        LineOfBusiness = LineOfBusiness,
        ColumnMap = ColumnMap is { Count: > 0 } ? new Dictionary<string, string>(ColumnMap, StringComparer.Ordinal) : null,
        ShadowAfterImport = ShadowAfterImport,
        ExpectedEvery = TimeSpan.FromHours(ExpectedEveryHours),
        StaleGrace = TimeSpan.FromHours(StaleGraceHours),
    };

    public static CpcdsDropSettingsRequest FromOptions(bool enabled, CpcdsDropTenantOptions options) => new()
    {
        Enabled = enabled,
        SourceSystem = options.SourceSystem,
        LineOfBusiness = options.LineOfBusiness,
        ColumnMap = options.ColumnMap,
        ShadowAfterImport = options.ShadowAfterImport,
        ExpectedEveryHours = (int)Math.Round(options.ExpectedEvery.TotalHours),
        StaleGraceHours = (int)Math.Round(options.StaleGrace.TotalHours),
    };
}

/// <summary>The tenant's drop as <c>GET .../drops/settings</c> describes it.</summary>
public sealed class CpcdsDropSettingsView
{
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Whether this deployment loads drops at all (<c>CpcdsDrop:Enabled</c>).</summary>
    public bool DropEnabledForDeployment { get; set; }

    /// <summary>Whether the tenant's settings turn its drop on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether files dropped now will load: the deployment and the tenant both on.</summary>
    public bool Loading { get; set; }

    public CpcdsDropSettingsSource Source { get; set; }

    /// <summary>Where files go, relative to the drop root (on a hosted SFTP drop, the tenant's own inbound folder).</summary>
    public string InboundFolder { get; set; } = string.Empty;

    public CpcdsDropSettingsRequest? Settings { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
