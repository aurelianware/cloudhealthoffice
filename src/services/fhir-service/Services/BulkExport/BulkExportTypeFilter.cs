using System.Globalization;
using System.Text.RegularExpressions;
using Hl7.Fhir.FhirPath;
using Hl7.Fhir.Model;

namespace FhirService.Services.BulkExport;

/// <summary>
/// Bulk Data <c>_typeFilter</c>: FHIR search queries (<c>Type?param=value&amp;…</c>)
/// that narrow what an export writes for a type.
///
/// <para>
/// The queries are judged against each resource as it is written, with the
/// FHIRPath expression of the R4 search parameter they name, so they mean
/// what the same search would mean on a FHIR server. Supported: token,
/// string and date parameters of the resource type (plus <c>_id</c>,
/// <c>_lastUpdated</c> and CARIN BB's ExplanationOfBenefit <c>type</c> and
/// <c>service-date</c>), no modifiers, a comma
/// for "any of" and the date prefixes <c>eq ne gt lt ge le</c>. Anything else
/// is refused at kick-off rather than ignored, so a client never mistakes an
/// unapplied filter for a filtered export.
/// </para>
///
/// <para>
/// Within one query every parameter must match; across queries for the same
/// type any one may match; a type no query names is exported unfiltered.
/// </para>
/// </summary>
public sealed partial class BulkExportTypeFilter
{
    private static readonly SearchParamType[] Supported =
        [SearchParamType.Token, SearchParamType.String, SearchParamType.Date];

    private static readonly string[] DatePrefixes = ["eq", "ne", "gt", "lt", "ge", "le"];

    private readonly IReadOnlyDictionary<string, IReadOnlyList<Query>> _byType;

    private BulkExportTypeFilter(IReadOnlyDictionary<string, IReadOnlyList<Query>> byType) => _byType = byType;

    /// <summary>A filter that keeps everything.</summary>
    public static BulkExportTypeFilter None { get; } = new(new Dictionary<string, IReadOnlyList<Query>>());

    /// <summary>True when <paramref name="resourceType"/> has a query to pass.</summary>
    public bool Filters(string resourceType) => _byType.ContainsKey(resourceType);

    /// <summary>
    /// Splits <c>_typeFilter</c> values into queries. Each value may itself
    /// hold several comma-separated queries; a comma only starts a new one
    /// when a resource type and <c>?</c> follow it, so a comma inside a query
    /// still means "any of".
    /// </summary>
    public static IReadOnlyList<string> Split(IEnumerable<string?>? values)
        => (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => QuerySeparator().Split(v!))
            .Select(q => q.Trim())
            .Where(q => q.Length > 0)
            .ToList();

    /// <summary>
    /// Parses and checks <paramref name="queries"/> against the types the
    /// export writes. Throws <see cref="BulkExportRequestException"/> naming
    /// the first query it cannot apply.
    /// </summary>
    public static BulkExportTypeFilter Parse(IReadOnlyList<string> queries, IReadOnlyList<string> exportedTypes)
    {
        if (queries.Count == 0)
            return None;

        var byType = new Dictionary<string, List<Query>>(StringComparer.Ordinal);
        foreach (var raw in queries)
        {
            var query = ParseQuery(raw, exportedTypes);
            if (!byType.TryGetValue(query.Type, out var list))
                byType[query.Type] = list = new List<Query>();
            list.Add(query);
        }

        return new BulkExportTypeFilter(byType.ToDictionary(p => p.Key, p => (IReadOnlyList<Query>)p.Value, StringComparer.Ordinal));
    }

    /// <summary>True when <paramref name="resource"/> passes any query for its type, or its type has none.</summary>
    public bool Matches(Resource resource)
        => !_byType.TryGetValue(resource.TypeName, out var queries) || queries.Any(q => q.Matches(resource));

    private static Query ParseQuery(string raw, IReadOnlyList<string> exportedTypes)
    {
        var mark = raw.IndexOf('?');
        if (mark <= 0 || mark == raw.Length - 1)
            throw new BulkExportRequestException($"_typeFilter '{raw}' must be a search query, e.g. ExplanationOfBenefit?status=active.");

        var type = raw[..mark];
        if (!exportedTypes.Contains(type, StringComparer.Ordinal))
            throw new BulkExportRequestException(
                $"_typeFilter '{raw}' names {type}, which this export does not write ({string.Join(", ", exportedTypes)}).");

        var conditions = new List<Condition>();
        foreach (var pair in raw[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
                throw new BulkExportRequestException($"_typeFilter '{raw}': '{pair}' is not name=value.");

            var name = Uri.UnescapeDataString(pair[..equals]);
            var value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            if (name.Contains(':') || name.Contains('.'))
                throw new BulkExportRequestException($"_typeFilter '{raw}': modifiers and chains ('{name}') are not supported.");

            var definition = Definition(type, name)
                ?? throw new BulkExportRequestException($"_typeFilter '{raw}': {type} has no search parameter '{name}'.");
            if (!Supported.Contains(definition.Type))
                throw new BulkExportRequestException(
                    $"_typeFilter '{raw}': '{name}' is a {definition.Type.ToString().ToLowerInvariant()} parameter; " +
                    "only token, string and date parameters are supported.");

            var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0)
                throw new BulkExportRequestException($"_typeFilter '{raw}': '{name}' has no value.");

            conditions.Add(new Condition(definition.Expression, definition.Type,
                values.Select(v => ParseValue(raw, definition.Type, v)).ToList()));
        }

        if (conditions.Count == 0)
            throw new BulkExportRequestException($"_typeFilter '{raw}' has no search parameters.");

        return new Query(type, conditions);
    }

    /// <summary>A search parameter: its type and the FHIRPath it selects.</summary>
    private sealed record ParamDefinition(SearchParamType Type, string Expression);

    /// <summary>
    /// Parameters the R4 base specification does not define but the IGs CHO
    /// serves do, so a client filtering what CHO claims can use them:
    /// CARIN BB's ExplanationOfBenefit <c>type</c> and <c>service-date</c>,
    /// and the common <c>_id</c> and <c>_lastUpdated</c> on every type.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Type, string Name), ParamDefinition> Additional =
        new Dictionary<(string, string), ParamDefinition>
        {
            [("ExplanationOfBenefit", "type")] = new(SearchParamType.Token, "ExplanationOfBenefit.type"),
            [("ExplanationOfBenefit", "service-date")] = new(SearchParamType.Date,
                "ExplanationOfBenefit.billablePeriod | ExplanationOfBenefit.item.serviced"),
        };

    private static ParamDefinition? Definition(string type, string name)
    {
        if (name == "_id")
            return new ParamDefinition(SearchParamType.Token, "id");
        if (name == "_lastUpdated")
            return new ParamDefinition(SearchParamType.Date, "meta.lastUpdated");
        if (Additional.TryGetValue((type, name), out var additional))
            return additional;

        var definition = ModelInfo.SearchParameters.FirstOrDefault(p =>
            string.Equals(p.Resource, type, StringComparison.Ordinal) && string.Equals(p.Name, name, StringComparison.Ordinal));
        return definition is null || string.IsNullOrEmpty(definition.Expression)
            ? null
            : new ParamDefinition(definition.Type, definition.Expression);
    }

    private static object ParseValue(string raw, SearchParamType type, string value) => type switch
    {
        SearchParamType.Token => value.Contains('|')
            ? new TokenValue(NullIfEmpty(value[..value.IndexOf('|')]), NullIfEmpty(value[(value.IndexOf('|') + 1)..]), SystemGiven: true)
            : new TokenValue(null, value, SystemGiven: false),
        SearchParamType.Date => ParseDateValue(raw, value),
        _ => value,
    };

    private static DateValue ParseDateValue(string raw, string value)
    {
        var prefix = value.Length > 2 && DatePrefixes.Contains(value[..2]) ? value[..2] : "eq";
        var text = value.Length > 2 && DatePrefixes.Contains(value[..2]) ? value[2..] : value;
        return TryRange(text) is { } range
            ? new DateValue(prefix, range.Low, range.High)
            : throw new BulkExportRequestException($"_typeFilter '{raw}': '{value}' is not a FHIR date.");
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// A FHIR date, dateTime or instant as the half-open range of instants it
    /// covers at its precision: 2026 is the whole year, 2026-03-02 the whole
    /// day. A dateTime without a time zone is read as UTC.
    /// </summary>
    internal static (DateTimeOffset Low, DateTimeOffset High)? TryRange(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        if (Regex.IsMatch(text, @"^\d{4}$")
            && DateTimeOffset.TryParseExact(text, "yyyy", CultureInfo.InvariantCulture, styles, out var year))
            return (year, year.AddYears(1));
        if (Regex.IsMatch(text, @"^\d{4}-\d{2}$")
            && DateTimeOffset.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, styles, out var month))
            return (month, month.AddMonths(1));
        if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$")
            && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, styles, out var day))
            return (day, day.AddDays(1));
        if (Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T")
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var instant))
        {
            // Precision of the stated time: seconds, or the fraction given.
            var fraction = Regex.Match(text, @"\.(\d+)");
            var step = fraction.Success
                ? TimeSpan.FromTicks((long)Math.Pow(10, Math.Max(0, 7 - Math.Min(7, fraction.Groups[1].Length))))
                : TimeSpan.FromSeconds(1);
            return (instant, instant + step);
        }
        return null;
    }

    [GeneratedRegex(@",(?=[A-Z][A-Za-z]+\?)")]
    private static partial Regex QuerySeparator();

    private sealed record Query(string Type, IReadOnlyList<Condition> Conditions)
    {
        public bool Matches(Resource resource) => Conditions.All(c => c.Matches(resource));
    }

    private sealed record TokenValue(string? System, string? Code, bool SystemGiven);

    private sealed record DateValue(string Prefix, DateTimeOffset Low, DateTimeOffset High);

    private sealed record Condition(string Expression, SearchParamType Type, IReadOnlyList<object> Values)
    {
        public bool Matches(Resource resource)
        {
            IReadOnlyList<Base> nodes;
            try
            {
                nodes = resource.Select(Expression, new FhirEvaluationContext()).OfType<Base>().ToList();
            }
            catch (Exception)
            {
                // An expression this resource cannot be evaluated against
                // (resolve() and the like) has nothing in it to match.
                return false;
            }

            return Values.Any(value => nodes.Any(node => Type switch
            {
                SearchParamType.Token => MatchesToken(node, (TokenValue)value),
                SearchParamType.String => MatchesString(node, (string)value),
                SearchParamType.Date => MatchesDate(node, (DateValue)value),
                _ => false,
            }));
        }
    }

    private static bool MatchesToken(Base node, TokenValue token) => node switch
    {
        CodeableConcept concept => concept.Coding.Any(c => MatchesToken(c, token)),
        Coding coding => SystemMatches(coding.System, token) && CodeMatches(coding.Code, token),
        Identifier identifier => SystemMatches(identifier.System, token) && CodeMatches(identifier.Value, token),
        ContactPoint contact => !token.SystemGiven && CodeMatches(contact.Value, token),
        PrimitiveType primitive => !(token.SystemGiven && token.System is not null)
                                   && CodeMatches(PrimitiveText(primitive), token),
        _ => false,
    };

    private static bool SystemMatches(string? system, TokenValue token)
        => !token.SystemGiven
           || (token.System is null ? string.IsNullOrEmpty(system) : string.Equals(system, token.System, StringComparison.Ordinal));

    private static bool CodeMatches(string? code, TokenValue token)
        => token.Code is null || string.Equals(code, token.Code, StringComparison.Ordinal);

    /// <summary>FHIR string search: the value starts with the text, ignoring case.</summary>
    private static bool MatchesString(Base node, string text)
        => StringsOf(node).Any(s => s.StartsWith(text, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> StringsOf(Base node) => node switch
    {
        PrimitiveType primitive => PrimitiveText(primitive) is { } s ? [s] : [],
        _ => node.Children.SelectMany(StringsOf),
    };

    private static string? PrimitiveText(PrimitiveType primitive) => primitive.ObjectValue switch
    {
        null => null,
        bool b => b ? "true" : "false",
        // An instant (meta.lastUpdated) holds the parsed value, not its text.
        DateTimeOffset instant => instant.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
        var v => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    private static bool MatchesDate(Base node, DateValue search)
    {
        var target = node switch
        {
            Period period => PeriodRange(period),
            PrimitiveType primitive => TryRange(PrimitiveText(primitive)),
            _ => null,
        };
        if (target is not { } t)
            return false;

        return search.Prefix switch
        {
            "eq" => t.Low >= search.Low && t.High <= search.High,
            "ne" => !(t.Low >= search.Low && t.High <= search.High),
            "gt" => t.High > search.High,
            "lt" => t.Low < search.Low,
            "ge" => t.High > search.Low,
            "le" => t.Low < search.High,
            _ => false,
        };
    }

    /// <summary>A Period's range; an open end is unbounded on that side.</summary>
    private static (DateTimeOffset Low, DateTimeOffset High)? PeriodRange(Period period)
    {
        var start = TryRange(period.Start);
        var end = TryRange(period.End);
        if (start is null && end is null)
            return null;
        return (start?.Low ?? DateTimeOffset.MinValue, end?.High ?? DateTimeOffset.MaxValue);
    }
}
