using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fluid;
using Fluid.Values;

namespace CorrespondenceService.Services;

/// <summary>
/// Liquid merge fields (Fluid). Letter text is plain text, never HTML, so
/// values are written as given. Every <c>{{ path }}</c> output must resolve to
/// a non-empty value unless it carries a <c>default</c> filter: a letter with a
/// blank member name or deadline must not go out.
/// </summary>
public static partial class MergeFields
{
    private static readonly FluidParser Parser = new();

    public static bool TryParse(string text, out IFluidTemplate? template, out string? error)
    {
        if (Parser.TryParse(text ?? string.Empty, out var parsed, out var message))
        {
            template = parsed;
            error = null;
            return true;
        }
        template = null;
        error = message;
        return false;
    }

    /// <summary>Renders <paramref name="text"/> against <paramref name="model"/>; adds any unresolved output paths to <paramref name="missing"/>.</summary>
    public static string Render(string text, IReadOnlyDictionary<string, object?> model, ISet<string> missing)
    {
        if (!TryParse(text, out var template, out var error))
            throw new InvalidOperationException($"Template text does not parse: {error}");

        foreach (var path in OutputPaths(text))
            if (!Resolves(model, path))
                missing.Add(path);

        var options = new TemplateOptions
        {
            CultureInfo = System.Globalization.CultureInfo.InvariantCulture,
            MaxSteps = 100_000,
        };
        // Missing values are found from the output paths above, not Fluid's
        // Undefined hook: that fires for conditions and defaulted fields too,
        // where an absent value is the template's intent.
        var context = new TemplateContext(options);
        foreach (var (key, value) in model)
            context.SetValue(key, value);
        // Letters are plain text, not HTML: values are written unchanged.
        return template!.Render(context, NullEncoder.Default).Trim();
    }

    /// <summary>
    /// Merge data from JSON as plain dictionaries, lists and primitives Fluid
    /// can walk. Numbers become decimal, so amounts print exactly.
    /// </summary>
    public static Dictionary<string, object?> FromJson(JsonObject? json)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (json is null) return result;
        foreach (var (key, value) in json)
            result[key] = Convert(value);
        return result;
    }

    private static object? Convert(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => FromJson(o),
        JsonArray a => a.Select(Convert).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        _ => node.ToJsonString(),
    };

    /// <summary>
    /// Paths printed with <c>{{ }}</c> and no <c>default</c> filter, minus
    /// names the template binds itself (for, assign, capture).
    /// </summary>
    internal static IReadOnlyList<string> OutputPaths(string text)
    {
        var bound = BoundNames().Matches(text).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (Match m in Output().Matches(text))
        {
            var expression = m.Groups["expr"].Value;
            if (expression.Contains("| default", StringComparison.Ordinal) || expression.Contains("|default", StringComparison.Ordinal))
                continue;
            var path = PathStart().Match(expression);
            if (!path.Success) continue; // a literal
            var root = path.Value.Split('.')[0];
            if (bound.Contains(root) || root is "forloop" or "true" or "false" or "nil" or "empty" or "blank") continue;
            paths.Add(path.Value);
        }
        return paths;
    }

    private static bool Resolves(IReadOnlyDictionary<string, object?> model, string path)
    {
        object? current = model;
        foreach (var segment in path.Split('.'))
        {
            if (current is IReadOnlyDictionary<string, object?> ro && ro.TryGetValue(segment, out var next1)) current = next1;
            else if (current is IDictionary<string, object?> d && d.TryGetValue(segment, out var next2)) current = next2;
            else return false;
        }
        return current switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            _ => true,
        };
    }

    [GeneratedRegex(@"\{\{-?\s*(?<expr>[^}]*?)\s*-?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Output();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*", RegexOptions.CultureInvariant)]
    private static partial Regex PathStart();

    [GeneratedRegex(@"\{%-?\s*(?:for|assign|capture)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex BoundNames();
}
