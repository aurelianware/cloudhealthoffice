using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

/// <summary>A search the directory cannot run as asked (FHIR answers 400).</summary>
public sealed class PlanNetSearchException(string message) : Exception(message);

/// <summary>One page of a directory search, with what was included alongside it.</summary>
public sealed record PlanNetSearchResult(
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<JsonObject> Matches,
    IReadOnlyList<JsonObject> Included,
    IReadOnlyList<KeyValuePair<string, string>> Applied,
    IReadOnlyList<string> Ignored);

/// <summary>
/// FHIR R4 search over a <see cref="PlanNetDirectorySnapshot"/>, implementing
/// what Plan-Net 1.2.0 asks of a directory server:
/// <list type="bullet">
///   <item>every SHALL parameter in <see cref="PlanNetSearchParameters"/>, plus
///   <c>_id</c> and <c>_lastUpdated</c>;</item>
///   <item>string (<c>:exact</c>, <c>:contains</c>), token (<c>system|code</c>,
///   <c>:text</c>, <c>:not</c>), reference (<c>Type/id</c>, bare id,
///   <c>:Type</c>) and date (all prefixes) matching, and <c>:missing</c>;</item>
///   <item>comma for OR within a parameter, repetition for AND;</item>
///   <item>forward chaining (<c>location.address-city=</c>) and reverse
///   chaining (<c>_has:PractitionerRole:practitioner:specialty=</c>), nested;</item>
///   <item><c>_include</c> and <c>_revinclude</c> (with <c>*</c>), <c>_count</c>
///   and <c>_page</c>.</item>
/// </list>
/// A parameter the directory does not know is ignored and reported back
/// (lenient handling, the FHIR default); an unsupported modifier or a value
/// that cannot be parsed is an error. Results are in id order.
/// </summary>
public static class PlanNetSearchEngine
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;
    private const int MaxChainDepth = 4;

    private static readonly HashSet<string> SilentlyIgnored = new(StringComparer.Ordinal)
    {
        "_format", "_pretty",
    };

    public static PlanNetSearchResult Search(
        PlanNetDirectorySnapshot snapshot, string resourceType, IEnumerable<KeyValuePair<string, string>> query)
    {
        var filters = new List<Func<JsonObject, bool>>();
        var applied = new List<KeyValuePair<string, string>>();
        var ignored = new List<string>();
        var includes = new List<string>();
        var revIncludes = new List<string>();
        var pageSize = DefaultPageSize;
        var page = 1;

        foreach (var (key, value) in query)
        {
            switch (key)
            {
                case "_count":
                    pageSize = Math.Clamp(ParseInt(key, value), 0, MaxPageSize);
                    continue;
                case "_page":
                    page = Math.Max(1, ParseInt(key, value));
                    continue;
                case "_include" or "_include:iterate":
                    includes.Add(value);
                    applied.Add(new(key, value));
                    continue;
                case "_revinclude" or "_revinclude:iterate":
                    revIncludes.Add(value);
                    applied.Add(new(key, value));
                    continue;
            }

            if (SilentlyIgnored.Contains(key)) continue;
            if (key.StartsWith('_') && !key.StartsWith("_has:", StringComparison.Ordinal)
                && !key.StartsWith("_id", StringComparison.Ordinal)
                && !key.StartsWith("_lastUpdated", StringComparison.Ordinal))
            {
                ignored.Add(key);
                continue;
            }

            var filter = Compile(snapshot, resourceType, key, value, depth: 0);
            if (filter is null)
            {
                ignored.Add(key);
                continue;
            }
            filters.Add(filter);
            applied.Add(new(key, value));
        }

        var matches = snapshot.OfType(resourceType).Where(r => filters.All(f => f(r))).ToList();
        var pageItems = pageSize == 0
            ? new List<JsonObject>()
            : matches.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var included = Include(snapshot, resourceType, pageItems, includes, revIncludes, ignored);
        return new PlanNetSearchResult(matches.Count, page, pageSize, pageItems, included, applied, ignored);
    }

    // ── filters ───────────────────────────────────────────────────────────

    private static Func<JsonObject, bool>? Compile(
        PlanNetDirectorySnapshot snapshot, string resourceType, string key, string value, int depth)
    {
        if (depth > MaxChainDepth)
            throw new PlanNetSearchException($"'{key}' chains more than {MaxChainDepth} levels.");

        if (key.StartsWith("_has:", StringComparison.Ordinal))
            return CompileReverseChain(snapshot, resourceType, key, value, depth);

        var dot = key.IndexOf('.');
        if (dot > 0)
            return CompileChain(snapshot, resourceType, key[..dot], key[(dot + 1)..], value, depth);

        var (name, modifier) = SplitModifier(key);
        var values = SplitOr(value);

        if (name == "_id")
        {
            // A reference to this type names an id too: the Inferno Plan-Net
            // kit searches _id=Practitioner/123 with the reference it found.
            var ids = values.Select(v => IdOf(resourceType, v)).ToHashSet(StringComparer.Ordinal);
            return r => ids.Contains(r["id"]?.GetValue<string>() ?? string.Empty);
        }

        if (name == "_lastUpdated")
        {
            var lastUpdated = NodePath.Of("meta", "lastUpdated");
            var dateMatch = DateMatcher(values);
            return r => lastUpdated.Select(r).Any(dateMatch);
        }

        var parameter = PlanNetSearchParameters.Find(resourceType, name);
        if (parameter is null) return null;

        if (modifier == "missing")
        {
            var missing = value switch
            {
                "true" => true,
                "false" => false,
                _ => throw new PlanNetSearchException($"'{key}' takes true or false."),
            };
            return r => !parameter.Values(r).Any() == missing;
        }

        Func<JsonNode, bool> match = parameter.Type switch
        {
            PlanNetSearchParamType.String => StringMatcher(key, modifier, values),
            PlanNetSearchParamType.Token => TokenMatcher(key, modifier, values),
            PlanNetSearchParamType.Reference => ReferenceMatcher(key, modifier, parameter, values),
            PlanNetSearchParamType.Date => modifier is null
                ? DateMatcher(values)
                : throw new PlanNetSearchException($"'{key}': date parameters take no modifier."),
            _ => throw new PlanNetSearchException($"'{key}' is not searchable."),
        };

        if (parameter.Type == PlanNetSearchParamType.Token && modifier == "not")
            return r => !parameter.Values(r).Any(match);
        return r => parameter.Values(r).Any(match);
    }

    /// <summary><c>ref[:Type].rest=value</c>: resources whose ref points at a target matching <c>rest=value</c>.</summary>
    private static Func<JsonObject, bool>? CompileChain(
        PlanNetDirectorySnapshot snapshot, string resourceType, string head, string rest, string value, int depth)
    {
        var (name, typeModifier) = SplitModifier(head);
        var parameter = PlanNetSearchParameters.Find(resourceType, name);
        if (parameter is null) return null;
        if (parameter.Type != PlanNetSearchParamType.Reference)
            throw new PlanNetSearchException($"'{name}' is not a reference parameter, so it cannot be chained.");

        var targets = typeModifier is null
            ? parameter.Targets
            : parameter.Targets.Contains(typeModifier)
                ? [typeModifier]
                : throw new PlanNetSearchException($"'{name}' cannot point at {typeModifier}.");

        var matchingTargets = new HashSet<string>(StringComparer.Ordinal);
        var known = false;
        foreach (var target in targets)
        {
            var inner = Compile(snapshot, target, rest, value, depth + 1);
            if (inner is null) continue;
            known = true;
            foreach (var resource in snapshot.OfType(target).Where(inner))
                matchingTargets.Add(PlanNetDirectorySnapshot.Key(resource));
        }
        if (!known) return null;

        return r => parameter.Values(r).Any(v => ReferenceKey(v) is { } k && matchingTargets.Contains(k));
    }

    /// <summary><c>_has:Source:ref:rest=value</c>: resources some matching Source points at through ref.</summary>
    private static Func<JsonObject, bool>? CompileReverseChain(
        PlanNetDirectorySnapshot snapshot, string resourceType, string key, string value, int depth)
    {
        var parts = key.Split(':', 4);
        if (parts.Length != 4 || parts[1].Length == 0 || parts[2].Length == 0 || parts[3].Length == 0)
            throw new PlanNetSearchException($"'{key}' is not of the form _has:Type:reference:parameter.");

        var (source, referenceName, rest) = (parts[1], parts[2], parts[3]);
        if (!PlanNetSearchParameters.IsDirectoryType(source)) return null;
        var reference = PlanNetSearchParameters.Find(source, referenceName);
        if (reference is null || reference.Type != PlanNetSearchParamType.Reference
            || !reference.Targets.Contains(resourceType))
            return null;

        var inner = Compile(snapshot, source, rest, value, depth + 1);
        if (inner is null) return null;

        var pointedAt = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in snapshot.OfType(source).Where(inner))
        {
            foreach (var target in reference.Values(resource))
                if (ReferenceKey(target) is { } k) pointedAt.Add(k);
        }
        return r => pointedAt.Contains(PlanNetDirectorySnapshot.Key(r));
    }

    // ── matchers ──────────────────────────────────────────────────────────

    private static Func<JsonNode, bool> StringMatcher(string key, string? modifier, IReadOnlyList<string> values)
    {
        switch (modifier)
        {
            case null:
                var prefixes = values.Select(Normalize).ToList();
                return node => Strings(node).Select(Normalize).Any(s => prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal)));
            case "contains":
                var fragments = values.Select(Normalize).ToList();
                return node => Strings(node).Select(Normalize).Any(s => fragments.Any(f => s.Contains(f, StringComparison.Ordinal)));
            case "exact":
                return node => Strings(node).Any(s => values.Contains(s, StringComparer.Ordinal));
            default:
                throw new PlanNetSearchException($"'{key}': string parameters support :exact and :contains.");
        }
    }

    private static Func<JsonNode, bool> TokenMatcher(string key, string? modifier, IReadOnlyList<string> values)
    {
        if (modifier == "text")
        {
            var prefixes = values.Select(Normalize).ToList();
            return node => TokenTexts(node).Select(Normalize).Any(s => prefixes.Any(p => s.StartsWith(p, StringComparison.Ordinal)));
        }
        if (modifier is not null && modifier != "not")
            throw new PlanNetSearchException($"'{key}': token parameters support :text and :not.");

        var wanted = values.Select(ParseToken).ToList();
        return node => Codes(node).Any(c => wanted.Any(w =>
            (w.System is null || (w.System.Length == 0 ? c.System is null : w.System == c.System))
            && (w.Code.Length == 0 || w.Code == c.Code)));
    }

    private static Func<JsonNode, bool> ReferenceMatcher(
        string key, string? modifier, PlanNetSearchParameter parameter, IReadOnlyList<string> values)
    {
        if (modifier is not null && !parameter.Targets.Contains(modifier))
            throw new PlanNetSearchException($"'{key}': {parameter.Name} cannot point at {modifier}.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            var normalized = NormalizeReference(raw);
            if (normalized.Contains('/'))
                keys.Add(normalized);
            else
                foreach (var target in modifier is null ? parameter.Targets : [modifier])
                    keys.Add($"{target}/{normalized}");
        }
        return node => ReferenceKey(node) is { } k && keys.Contains(k);
    }

    private static Func<JsonNode, bool> DateMatcher(IReadOnlyList<string> values)
    {
        var wanted = values.Select(ParseDateQuery).ToList();
        return node =>
        {
            var target = DateRange(node);
            return target is { } t && wanted.Any(w => Compare(w.Prefix, t, w.Range));
        };
    }

    private static bool Compare(string prefix, (DateTimeOffset Start, DateTimeOffset End) t, (DateTimeOffset Start, DateTimeOffset End) q)
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

    // ── _include / _revinclude ────────────────────────────────────────────

    private static List<JsonObject> Include(
        PlanNetDirectorySnapshot snapshot,
        string resourceType,
        IReadOnlyList<JsonObject> page,
        IReadOnlyList<string> includes,
        IReadOnlyList<string> revIncludes,
        List<string> ignored)
    {
        var seen = new HashSet<string>(page.Select(PlanNetDirectorySnapshot.Key), StringComparer.Ordinal);
        var included = new List<JsonObject>();
        void Add(JsonObject resource)
        {
            if (seen.Add(PlanNetDirectorySnapshot.Key(resource))) included.Add(resource);
        }

        foreach (var include in includes)
        {
            var parts = include.Split(':');
            if (parts.Length is < 2 or > 3 || parts[0] != resourceType)
            {
                ignored.Add($"_include={include}");
                continue;
            }
            var parameters = ReferenceParameters(resourceType, parts[1]);
            if (parameters.Count == 0)
            {
                ignored.Add($"_include={include}");
                continue;
            }
            var targetType = parts.Length == 3 ? parts[2] : null;
            foreach (var resource in page)
            foreach (var parameter in parameters)
            foreach (var reference in parameter.Values(resource))
            {
                if (ReferenceKey(reference) is not { } k) continue;
                if (targetType is not null && !k.StartsWith(targetType + "/", StringComparison.Ordinal)) continue;
                if (snapshot.Resolve(k) is { } target) Add(target);
            }
        }

        if (revIncludes.Count > 0 && page.Count > 0)
        {
            var pageKeys = new HashSet<string>(page.Select(PlanNetDirectorySnapshot.Key), StringComparer.Ordinal);
            foreach (var revInclude in revIncludes)
            {
                var parts = revInclude.Split(':');
                if (parts.Length is < 2 or > 3 || !PlanNetSearchParameters.IsDirectoryType(parts[0]))
                {
                    ignored.Add($"_revinclude={revInclude}");
                    continue;
                }
                var parameters = ReferenceParameters(parts[0], parts[1])
                    .Where(p => p.Targets.Contains(resourceType)).ToList();
                if (parameters.Count == 0)
                {
                    ignored.Add($"_revinclude={revInclude}");
                    continue;
                }
                foreach (var source in snapshot.OfType(parts[0]))
                {
                    if (parameters.Any(p => p.Values(source).Any(v => ReferenceKey(v) is { } k && pageKeys.Contains(k))))
                        Add(source);
                }
            }
        }

        return included;
    }

    private static List<PlanNetSearchParameter> ReferenceParameters(string resourceType, string name)
    {
        var all = PlanNetSearchParameters.For(resourceType).Values
            .Where(p => p.Type == PlanNetSearchParamType.Reference);
        return name == "*"
            ? all.ToList()
            : all.Where(p => p.Name == name).ToList();
    }

    // ── value helpers ─────────────────────────────────────────────────────

    /// <summary>The literal reference a Reference names, as <c>Type/id</c>, or null.</summary>
    public static string? ReferenceKey(JsonNode node)
    {
        if (node is not JsonObject obj || obj["reference"] is not JsonValue value
            || !value.TryGetValue<string>(out var reference) || string.IsNullOrEmpty(reference))
            return null;
        var normalized = NormalizeReference(reference);
        return normalized.Contains('/') ? normalized : null;
    }

    /// <summary>The id an <c>_id</c> value names: itself, or the id of a reference to <paramref name="resourceType"/>.</summary>
    private static string IdOf(string resourceType, string value)
    {
        if (!value.Contains('/')) return value;
        var reference = NormalizeReference(value);
        var slash = reference.IndexOf('/');
        // A reference to another type names nothing of this type.
        return reference[..slash] == resourceType ? reference[(slash + 1)..] : string.Empty;
    }

    /// <summary><c>Type/id</c> from a relative, absolute or versioned reference; a bare id unchanged.</summary>
    private static string NormalizeReference(string reference)
    {
        var history = reference.IndexOf("/_history/", StringComparison.Ordinal);
        if (history >= 0) reference = reference[..history];
        var segments = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[^2]}/{segments[^1]}" : reference;
    }

    private static IEnumerable<string> Strings(JsonNode node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                yield return s;
                break;
            case JsonObject obj:
                foreach (var field in new[] { "text", "family", "given", "prefix", "suffix", "line", "city", "district", "state", "postalCode", "country" })
                {
                    switch (obj[field])
                    {
                        case JsonValue fv when fv.TryGetValue<string>(out var fs):
                            yield return fs;
                            break;
                        case JsonArray fa:
                            foreach (var item in fa)
                                if (item is JsonValue iv && iv.TryGetValue<string>(out var its)) yield return its;
                            break;
                    }
                }
                break;
        }
    }

    private static IEnumerable<(string? System, string Code)> Codes(JsonNode node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                yield return (null, s);
                break;
            case JsonValue b when b.TryGetValue<bool>(out var flag):
                yield return (null, flag ? "true" : "false");
                break;
            case JsonObject obj when obj["coding"] is JsonArray codings:
                foreach (var coding in codings.OfType<JsonObject>())
                    if (Text(coding, "code") is { } code) yield return (Text(coding, "system"), code);
                break;
            case JsonObject obj when Text(obj, "code") is { } code:
                yield return (Text(obj, "system"), code);
                break;
            case JsonObject obj when Text(obj, "value") is { } identifierValue:
                yield return (Text(obj, "system"), identifierValue);
                break;
        }
    }

    private static IEnumerable<string> TokenTexts(JsonNode node)
    {
        if (node is not JsonObject obj) yield break;
        if (Text(obj, "text") is { } text) yield return text;
        if (Text(obj, "display") is { } display) yield return display;
        if (obj["coding"] is JsonArray codings)
            foreach (var coding in codings.OfType<JsonObject>())
                if (Text(coding, "display") is { } d) yield return d;
        if (obj["type"] is JsonObject type && Text(type, "text") is { } typeText) yield return typeText;
    }

    private static string? Text(JsonObject obj, string field)
        => obj[field] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static (string? System, string Code) ParseToken(string value)
    {
        var bar = IndexOfUnescaped(value, '|');
        return bar < 0
            ? (null, Unescape(value))
            : (Unescape(value[..bar]), Unescape(value[(bar + 1)..]));
    }

    private static (DateTimeOffset Start, DateTimeOffset End)? DateRange(JsonNode node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
            return TryParseDate(s, out var range) ? range : null;
        if (node is JsonObject period)
        {
            var start = Text(period, "start") is { } ps && TryParseDate(ps, out var sr) ? sr.Start : DateTimeOffset.MinValue;
            var end = Text(period, "end") is { } pe && TryParseDate(pe, out var er) ? er.End : DateTimeOffset.MaxValue;
            return (start, end);
        }
        return null;
    }

    private static (string Prefix, (DateTimeOffset Start, DateTimeOffset End) Range) ParseDateQuery(string value)
    {
        var prefix = "eq";
        if (value.Length > 2 && char.IsLetter(value[0]) && char.IsLetter(value[1]))
        {
            prefix = value[..2];
            value = value[2..];
            if (prefix is not ("eq" or "ne" or "gt" or "lt" or "ge" or "le" or "sa" or "eb" or "ap"))
                throw new PlanNetSearchException($"'{prefix}' is not a date prefix.");
        }
        return TryParseDate(value, out var range)
            ? (prefix, range)
            : throw new PlanNetSearchException($"'{value}' is not a FHIR date.");
    }

    /// <summary>The span a FHIR date, dateTime or instant covers, at its own precision.</summary>
    private static bool TryParseDate(string value, out (DateTimeOffset Start, DateTimeOffset End) range)
    {
        range = default;
        var styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        switch (value.Length)
        {
            case 4 when DateTime.TryParseExact(value, "yyyy", CultureInfo.InvariantCulture, styles, out var y):
                range = (new DateTimeOffset(y, TimeSpan.Zero), new DateTimeOffset(y.AddYears(1), TimeSpan.Zero));
                return true;
            case 7 when DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, styles, out var m):
                range = (new DateTimeOffset(m, TimeSpan.Zero), new DateTimeOffset(m.AddMonths(1), TimeSpan.Zero));
                return true;
            case 10 when DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, styles, out var d):
                range = (new DateTimeOffset(d, TimeSpan.Zero), new DateTimeOffset(d.AddDays(1), TimeSpan.Zero));
                return true;
        }
        if (value.Length > 10 && value[10] == 'T'
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            // Precision of the value given: to the minute or second (or finer).
            var span = value.Length <= 16 || (value.Length > 16 && value[16] != ':')
                ? TimeSpan.FromMinutes(1)
                : value.Length > 19 && value[19] == '.' ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(1);
            range = (instant, instant + span);
            return true;
        }
        return false;
    }

    private static (string Name, string? Modifier) SplitModifier(string key)
    {
        var colon = key.IndexOf(':');
        return colon < 0 ? (key, null) : (key[..colon], key[(colon + 1)..]);
    }

    private static IReadOnlyList<string> SplitOr(string value)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\') { i++; continue; }
            if (value[i] != ',') continue;
            parts.Add(value[start..i]);
            start = i + 1;
        }
        parts.Add(value[start..]);
        // Escapes other than \, are kept for the token parser, which splits on \|.
        return parts.Select(p => p.Replace("\\,", ",", StringComparison.Ordinal)).ToList();
    }

    private static int IndexOfUnescaped(string value, char c)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\') { i++; continue; }
            if (value[i] == c) return i;
        }
        return -1;
    }

    private static string Unescape(string value)
        => value.Replace("\\|", "|", StringComparison.Ordinal)
            .Replace("\\$", "$", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    /// <summary>Case- and accent-insensitive form for string matching, as FHIR asks.</summary>
    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static int ParseInt(string key, string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new PlanNetSearchException($"'{key}' takes a whole number.");
}
