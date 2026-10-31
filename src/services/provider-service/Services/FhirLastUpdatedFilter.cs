using System.Globalization;

namespace ProviderService.Services;

/// <summary>
/// The FHIR <c>_lastUpdated</c> search parameter over a resource's
/// <c>meta.lastUpdated</c>. Each value is a date, dateTime or instant with
/// an optional prefix (eq, ne, gt, lt, ge, le, sa, eb, ap) and covers the
/// span of its own precision, so an instant given to the millisecond
/// matches the resource last updated within that millisecond. Repeated
/// parameters must all match (FHIR AND semantics); comma-separated values
/// within one parameter are alternatives (OR).
/// </summary>
public sealed class FhirLastUpdatedFilter
{
    private static readonly string[] Prefixes = ["eq", "ne", "gt", "lt", "ge", "le", "sa", "eb", "ap"];

    private readonly List<List<(string Prefix, DateTimeOffset Start, DateTimeOffset End)>> _clauses;

    private FhirLastUpdatedFilter(List<List<(string, DateTimeOffset, DateTimeOffset)>> clauses) => _clauses = clauses;

    /// <summary>Null when no value was given; throws <see cref="FormatException"/> for a value that is not a FHIR date.</summary>
    public static FhirLastUpdatedFilter? Parse(IEnumerable<string>? parameters)
    {
        var clauses = new List<List<(string, DateTimeOffset, DateTimeOffset)>>();
        foreach (var parameter in parameters ?? [])
        {
            if (string.IsNullOrWhiteSpace(parameter)) continue;
            clauses.Add(parameter.Split(',').Select(ParseValue).ToList());
        }
        return clauses.Count == 0 ? null : new FhirLastUpdatedFilter(clauses);
    }

    public bool Matches(DateTime lastUpdatedUtc)
    {
        var instant = new DateTimeOffset(DateTime.SpecifyKind(lastUpdatedUtc, DateTimeKind.Utc));
        var target = (Start: instant, End: instant.AddTicks(1));
        return _clauses.All(alternatives => alternatives.Any(q => Compare(q.Prefix, target, (q.Start, q.End))));
    }

    private static (string, DateTimeOffset, DateTimeOffset) ParseValue(string value)
    {
        value = value.Trim();
        var prefix = "eq";
        if (value.Length > 2 && char.IsLetter(value[0]) && char.IsLetter(value[1]))
        {
            prefix = value[..2];
            value = value[2..];
            if (!Prefixes.Contains(prefix)) throw new FormatException($"'{prefix}' is not a date prefix.");
        }
        return TryParseDate(value, out var start, out var end)
            ? (prefix, start, end)
            : throw new FormatException($"'{value}' is not a FHIR date.");
    }

    private static bool Compare(
        string prefix, (DateTimeOffset Start, DateTimeOffset End) t, (DateTimeOffset Start, DateTimeOffset End) q)
    {
        var eq = t.Start >= q.Start && t.End <= q.End;
        return prefix switch
        {
            "eq" => eq,
            "ne" => !eq,
            "gt" => t.End > q.End,
            "lt" => t.Start < q.Start,
            "ge" => t.End > q.End || eq,
            "le" => t.Start < q.Start || eq,
            "sa" => t.Start >= q.End,
            "eb" => t.End <= q.Start,
            "ap" => t.Start < q.End && t.End > q.Start,
            _ => false,
        };
    }

    private static bool TryParseDate(string value, out DateTimeOffset start, out DateTimeOffset end)
    {
        start = end = default;
        var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        switch (value.Length)
        {
            case 4 when DateTime.TryParseExact(value, "yyyy", CultureInfo.InvariantCulture, styles, out var y):
                (start, end) = (new DateTimeOffset(y, TimeSpan.Zero), new DateTimeOffset(y.AddYears(1), TimeSpan.Zero));
                return true;
            case 7 when DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, styles, out var m):
                (start, end) = (new DateTimeOffset(m, TimeSpan.Zero), new DateTimeOffset(m.AddMonths(1), TimeSpan.Zero));
                return true;
            case 10 when DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, styles, out var d):
                (start, end) = (new DateTimeOffset(d, TimeSpan.Zero), new DateTimeOffset(d.AddDays(1), TimeSpan.Zero));
                return true;
        }
        if (value.Length > 10 && value[10] == 'T'
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            // The precision the value was given at: minute, second, or the
            // fraction's own digits (so a seven-digit fraction is one tick).
            TimeSpan span;
            if (value.Length <= 16 || value[16] != ':') span = TimeSpan.FromMinutes(1);
            else if (value.Length > 19 && value[19] == '.')
            {
                var digits = value.Skip(20).TakeWhile(char.IsDigit).Count();
                span = TimeSpan.FromTicks((long)Math.Pow(10, 7 - Math.Clamp(digits, 1, 7)));
            }
            else span = TimeSpan.FromSeconds(1);
            (start, end) = (instant, instant + span);
            return true;
        }
        return false;
    }
}
