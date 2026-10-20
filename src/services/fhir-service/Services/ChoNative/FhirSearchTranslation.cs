using System.Globalization;

namespace FhirService.Services.ChoNative;

/// <summary>
/// Turns FHIR search parameter values into the plain filters CHO's domain
/// services take (ADR 018). Everything here either resolves to a filter or
/// throws <see cref="FhirSearchNotSupportedException"/>; nothing is ignored.
/// </summary>
public static class FhirSearchTranslation
{
    /// <summary>A half-open UTC range: <c>From</c> inclusive, <c>To</c> exclusive; either may be open.</summary>
    public readonly record struct DateRange(DateTime? From, DateTime? To)
    {
        /// <summary>The intersection of two ranges (both conditions must hold).</summary>
        public DateRange Intersect(DateRange other) => new(
            Max(From, other.From),
            Min(To, other.To));

        private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a > b ? a : b);
        private static DateTime? Min(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : (a < b ? a : b);

        /// <summary>True when no instant can satisfy the range.</summary>
        public bool IsEmpty => From is { } f && To is { } t && f >= t;
    }

    /// <summary>
    /// A FHIR date search value with an optional prefix (eq, ge, gt, le, lt) at
    /// year, month, day or instant precision, as the range of instants it
    /// matches. A value with no time zone is read as UTC. Other prefixes
    /// (ne, sa, eb, ap) are not supported.
    /// </summary>
    public static DateRange ParseDate(string parameter, string raw)
    {
        var value = raw.Trim();
        var prefix = "eq";
        if (value.Length > 2 && char.IsLetter(value[0]) && char.IsLetter(value[1]))
        {
            prefix = value[..2].ToLowerInvariant();
            value = value[2..];
        }

        var (start, end) = ParseInterval(parameter, value);
        return prefix switch
        {
            "eq" => new DateRange(start, end),
            "ge" => new DateRange(start, null),
            "gt" => new DateRange(end, null),
            "le" => new DateRange(null, end),
            "lt" => new DateRange(null, start),
            _ => throw new FhirSearchNotSupportedException(
                $"The '{prefix}' prefix is not supported on {parameter}; use eq, ge, gt, le or lt."),
        };
    }

    /// <summary>Every value of a repeated date parameter must hold (FHIR AND semantics).</summary>
    public static DateRange? ParseDates(string parameter, IEnumerable<string?> values)
    {
        DateRange? range = null;
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.Contains(','))
                throw new FhirSearchNotSupportedException($"{parameter} does not support OR (comma-separated) values.");
            var parsed = ParseDate(parameter, raw);
            range = range is null ? parsed : range.Value.Intersect(parsed);
        }
        return range;
    }

    /// <summary>FHIR dateTime/instant forms with a time part (seconds optional, zone optional; no zone means UTC).</summary>
    private static readonly string[] InstantFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm",
    ];

    private static (DateTime Start, DateTime End) ParseInterval(string parameter, string value)
    {
        try
        {
            return ParseIntervalCore(parameter, value);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The end of the matched interval is past DateTime.MaxValue (year 9999).
            throw new FhirSearchNotSupportedException($"{parameter} value is outside the supported date range.");
        }
    }

    private static (DateTime Start, DateTime End) ParseIntervalCore(string parameter, string value)
    {
        var inv = CultureInfo.InvariantCulture;
        if (value.Length == 4 && DateTime.TryParseExact(value, "yyyy", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var year))
            return (Utc(year), Utc(year).AddYears(1));
        if (value.Length == 7 && DateTime.TryParseExact(value, "yyyy-MM", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var month))
            return (Utc(month), Utc(month).AddMonths(1));
        if (value.Length == 10 && DateTime.TryParseExact(value, "yyyy-MM-dd", inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day))
            return (Utc(day), Utc(day).AddDays(1));
        if (value.Length > 10 && DateTimeOffset.TryParseExact(value, InstantFormats, inv, DateTimeStyles.AssumeUniversal, out var instant))
        {
            var utc = instant.UtcDateTime;
            // An instant matches the second it names (FHIR precision of the value).
            var second = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            return (second, second.AddSeconds(1));
        }

        throw new FhirSearchNotSupportedException(
            $"'{value}' is not a valid {parameter} value (expected YYYY, YYYY-MM, YYYY-MM-DD or an instant).");
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>
    /// A token value <c>[system|]code</c>. Returns the code when the system is
    /// absent or one of <paramref name="acceptedSystems"/>; throws for any other
    /// system, because this server cannot match it.
    /// </summary>
    public static string ParseToken(string parameter, string raw, params string[] acceptedSystems)
    {
        var value = raw.Trim();
        var bar = value.IndexOf('|');
        if (bar < 0) return value;

        var system = value[..bar];
        var code = value[(bar + 1)..];
        if (system.Length == 0 || acceptedSystems.Contains(system, StringComparer.Ordinal))
            return code;

        throw new FhirSearchNotSupportedException(
            $"{parameter} system '{system}' is not searchable on this server"
            + (acceptedSystems.Length > 0 ? $" (supported: {string.Join(", ", acceptedSystems)})." : "."));
    }

    /// <summary>
    /// A reference search value naming a Patient: <c>123</c>, <c>Patient/123</c>
    /// or an absolute URL ending in <c>Patient/123</c>. Any other resource type
    /// is refused.
    /// </summary>
    public static string ParsePatientReference(string parameter, string raw)
    {
        var value = raw.Trim().TrimEnd('/');
        var slash = value.LastIndexOf('/');
        if (slash < 0) return value;

        var typeEnd = slash;
        var typeStart = value.LastIndexOf('/', typeEnd - 1) + 1;
        var type = value[typeStart..typeEnd];
        if (!string.Equals(type, "Patient", StringComparison.Ordinal))
            throw new FhirSearchNotSupportedException($"{parameter} must reference a Patient, not '{type}'.");
        return value[(slash + 1)..];
    }

    /// <summary>
    /// The single value a parameter may take on this server: repeated values
    /// (AND) and comma-separated values (OR) are refused for parameters that
    /// map to one exact filter.
    /// </summary>
    public static string? Single(string parameter, IEnumerable<string?> values)
    {
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (present.Count == 0) return null;
        if (present.Count > 1)
            throw new FhirSearchNotSupportedException($"{parameter} may be given only once.");
        if (present[0].Contains(','))
            throw new FhirSearchNotSupportedException($"{parameter} does not support OR (comma-separated) values.");
        return present[0];
    }

    /// <summary>Comma-separated OR values of a token parameter, given once.</summary>
    public static IReadOnlyList<string> AnyOf(string parameter, IEnumerable<string?> values)
    {
        var present = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        if (present.Count == 0) return [];
        if (present.Count > 1)
            throw new FhirSearchNotSupportedException($"{parameter} may be given only once (use commas for OR).");
        return present[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>A UTC instant for an upstream query string.</summary>
    public static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
