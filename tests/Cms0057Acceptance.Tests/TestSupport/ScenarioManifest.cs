using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// Loader + model for the CMS-0057-F scenario manifest
/// (<c>scenarios.json</c>) — the machine-readable source of truth for scenario
/// status. Shared by the drift tests here and, by the same JSON file, the
/// evidence generator in <c>tools/Cms0057Evidence</c>.
/// </summary>
internal static class ScenarioManifest
{
    public const string ReplaceBackend = "Replace";

    /// <summary>The only statuses a scenario may declare.</summary>
    public static readonly IReadOnlySet<string> AllowedStatuses =
        new HashSet<string>(StringComparer.Ordinal) { "PASSABLE", "PARTIAL", "GAP", "N/A" };

    public static string ManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "scenarios.json");

    public static ManifestDocument Load()
    {
        var json = File.ReadAllText(ManifestPath);
        var doc = JsonSerializer.Deserialize<ManifestDocument>(json, JsonOpts)
                  ?? throw new InvalidOperationException("scenarios.json deserialized to null");
        return doc;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── Model ────────────────────────────────────────────────────────────────

    public sealed class ManifestDocument
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
        [JsonPropertyName("scenarios")] public List<ScenarioEntry> Scenarios { get; init; } = new();
    }

    public sealed class ScenarioEntry
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("capability")] public string Capability { get; init; } = "";
        [JsonPropertyName("replace")] public BackendStatus Replace { get; init; } = new();
        [JsonPropertyName("augment")] public Dictionary<string, BackendStatus> Augment { get; init; } = new();
    }

    public sealed class BackendStatus
    {
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("rationale")] public string? Rationale { get; init; }
    }
}

/// <summary>
/// A single scenario/backend association discovered from an xUnit test's
/// <c>[Trait]</c> attributes.
/// </summary>
internal sealed record ScenarioTrait(string TestName, string ScenarioId, string Backend, bool IsGap);

/// <summary>
/// Scans a test assembly's <c>[Trait]</c> attributes and projects every test
/// carrying a <c>Scenario</c> trait into <see cref="ScenarioTrait"/> rows.
/// Backend defaults to Replace (product capability) when no Backend trait is
/// present. Reads trait constructor arguments via reflection so no xUnit
/// discovery API is needed.
/// </summary>
internal static class TraitScanner
{
    private const string ScenarioTraitName = "Scenario";
    private const string BackendTraitName = "Backend";
    private const string KindTraitName = "Kind";

    public static IReadOnlyList<ScenarioTrait> Scan(Assembly assembly)
    {
        var results = new List<ScenarioTrait>();

        foreach (var type in assembly.GetTypes())
        {
            var classTraits = ReadTraits(type.GetCustomAttributesData());

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var methodTraits = ReadTraits(method.GetCustomAttributesData());
                if (methodTraits.Count == 0 && classTraits.Count == 0) continue;

                // Method-level traits win; fall back to class-level.
                string? scenario = FirstValue(methodTraits, ScenarioTraitName)
                                   ?? FirstValue(classTraits, ScenarioTraitName);
                if (scenario is null) continue;

                var backend = FirstValue(methodTraits, BackendTraitName)
                              ?? FirstValue(classTraits, BackendTraitName)
                              ?? ScenarioManifest.ReplaceBackend;

                var isGap = HasValue(methodTraits, KindTraitName, "GAP")
                            || HasValue(classTraits, KindTraitName, "GAP");

                results.Add(new ScenarioTrait(
                    $"{type.FullName}.{method.Name}", scenario, backend, isGap));
            }
        }

        return results;
    }

    private static List<(string Name, string Value)> ReadTraits(IEnumerable<CustomAttributeData> attrs)
    {
        var list = new List<(string, string)>();
        foreach (var a in attrs)
        {
            if (a.AttributeType.Name != "TraitAttribute") continue;
            if (a.ConstructorArguments.Count != 2) continue;
            var name = a.ConstructorArguments[0].Value as string;
            var value = a.ConstructorArguments[1].Value as string;
            if (name is not null && value is not null) list.Add((name, value));
        }
        return list;
    }

    private static string? FirstValue(List<(string Name, string Value)> traits, string name) =>
        traits.FirstOrDefault(t => t.Name == name).Value;

    private static bool HasValue(List<(string Name, string Value)> traits, string name, string value) =>
        traits.Any(t => t.Name == name && string.Equals(t.Value, value, StringComparison.OrdinalIgnoreCase));
}
