using System.Text.Json;

namespace CloudHealthOffice.PlanConfiguration;

/// <summary>
/// The levels a setting can be set at, least to most specific
/// (plan-configuration.md §3). The numbers order resolution and leave room:
/// a new level (carrier, program, benefit plan, sponsor policy, class) is a
/// new member here and in a definition's <see cref="SettingDefinition.AllowedLevels"/>,
/// not a change to the resolver. Only <see cref="Tenant"/>,
/// <see cref="LineOfBusiness"/> and <see cref="Sponsor"/> hold values today
/// (step 1); the platform default lives in the definition.
/// </summary>
public enum SettingLevel
{
    Platform = 0,
    Tenant = 100,
    Carrier = 200,
    /// <summary>A line-of-business type (Commercial, Medicaid ...). Programs, which carry a type, take its place in step 2.</summary>
    LineOfBusiness = 300,
    Program = 400,
    BenefitPlan = 500,
    /// <summary>Scope id: the sponsor's group number.</summary>
    Sponsor = 600,
    SponsorPolicy = 700,
    Class = 800,
}

public enum SettingType
{
    Integer,
    Decimal,
    Boolean,
    /// <summary>One of <see cref="SettingConstraints.AllowedValues"/>.</summary>
    Enum,
    /// <summary>An ISO 8601 duration or a TimeSpan string, e.g. <c>"P30D"</c> or <c>"30.00:00:00"</c>.</summary>
    Duration,
    /// <summary>A small structured value, stored as JSON.</summary>
    Json,
}

public enum ChangeControl
{
    None,
    ReasonRequired,
    /// <summary>A change waits for a second person with <c>config:approve</c>.</summary>
    TwoPersonApproval,
}

/// <summary>
/// A limit from law or contract, scoped to a line-of-business type (or all
/// when <see cref="LineOfBusiness"/> is null). Writes that break it are
/// refused where the line of business is known; resolution clamps to it and
/// says so (plan-configuration.md §5).
/// </summary>
public sealed record SettingFloor(string? LineOfBusiness, decimal? Min, decimal? Max, string Citation);

public sealed record SettingConstraints
{
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
    public IReadOnlyList<string>? AllowedValues { get; init; }
    public IReadOnlyList<SettingFloor> Floors { get; init; } = [];
}

/// <summary>
/// A business setting, defined in code by the service that owns it
/// (plan-configuration.md §4). Values are JSON text: <c>7</c>, <c>true</c>,
/// <c>"Weekly"</c>.
/// </summary>
public sealed record SettingDefinition
{
    /// <summary><c>{owner area}.{name}</c>, e.g. <c>enrollment.orphan.graceDays</c>.</summary>
    public required string Key { get; init; }
    public required SettingType Type { get; init; }
    /// <summary>The owning service.</summary>
    public required string Owner { get; init; }
    public required string Description { get; init; }
    /// <summary>Unit shown with the value, e.g. "days".</summary>
    public string? Unit { get; init; }
    /// <summary>The platform default, JSON.</summary>
    public required string Default { get; init; }
    /// <summary>Platform defaults per line-of-business type (canonical names, <see cref="LinesOfBusiness"/>).</summary>
    public IReadOnlyDictionary<string, string> DefaultsByLineOfBusiness { get; init; } = new Dictionary<string, string>();
    /// <summary>Where a value may be set. <see cref="SettingLevel.Platform"/> is never settable.</summary>
    public required IReadOnlySet<SettingLevel> AllowedLevels { get; init; }
    public SettingConstraints Constraints { get; init; } = new();
    public ChangeControl ChangeControl { get; init; } = ChangeControl.None;
    /// <summary>Whether values follow the transaction's date; most do.</summary>
    public bool EffectiveDated { get; init; } = true;

    /// <summary>The platform default for a line of business (the general default when the line has none).</summary>
    public string DefaultFor(string? lineOfBusiness) =>
        lineOfBusiness is not null
        && DefaultsByLineOfBusiness.FirstOrDefault(d => string.Equals(d.Key, lineOfBusiness, StringComparison.OrdinalIgnoreCase)) is { Value: { } v }
            ? v
            : Default;
}

/// <summary>Canonical line-of-business type names, as sponsor-service's <c>LineOfBusiness</c> enum spells them.</summary>
public static class LinesOfBusiness
{
    public const string Commercial = "Commercial";
    public const string Medicare = "Medicare";
    public const string Medicaid = "Medicaid";
    public const string Exchange = "Exchange";
    public const string Tricare = "TRICARE";
    public const string Va = "VA";

    private static readonly string[] ByNumber = ["", Commercial, Medicare, Medicaid, Exchange, Tricare, Va];

    public static IReadOnlyList<string> All { get; } = ByNumber.Skip(1).ToArray();

    /// <summary>The canonical name for a value, numeric (sponsor-service's default JSON) or by name; null when unknown.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, out var n)) return n > 0 && n < ByNumber.Length ? ByNumber[n] : null;
        return All.FirstOrDefault(name => string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Reading typed values out of setting JSON.</summary>
public static class SettingJson
{
    public static bool TryParse(SettingType type, string? json, out JsonElement element, out string? error)
    {
        element = default;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "A value is required."; return false; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = "The value is not valid JSON (send 7, true or \"text\").";
            return false;
        }

        var ok = type switch
        {
            SettingType.Integer => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            SettingType.Decimal => element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out _),
            SettingType.Boolean => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            SettingType.Enum => element.ValueKind == JsonValueKind.String,
            SettingType.Duration => element.ValueKind == JsonValueKind.String && TryDuration(element.GetString(), out _),
            SettingType.Json => true,
            _ => false,
        };
        if (!ok) error = $"The value must be {Describe(type)}.";
        return ok;
    }

    public static decimal? AsNumber(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetDecimal(out var d) ? d : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Canonical JSON for a number (no exponent, no trailing zeros).</summary>
    public static string Number(decimal value) => value.ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryDuration(string? text, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out duration)) return true;
        try
        {
            duration = System.Xml.XmlConvert.ToTimeSpan(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Describe(SettingType type) => type switch
    {
        SettingType.Integer => "a whole number",
        SettingType.Decimal => "a number",
        SettingType.Boolean => "true or false",
        SettingType.Enum => "one of the allowed values, as a string",
        SettingType.Duration => "a duration such as \"P30D\"",
        _ => "JSON",
    };
}
