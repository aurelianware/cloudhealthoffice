using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cms0057Evidence;

/// <summary>Allowed declared statuses.</summary>
public static class Status
{
    public const string Passable = "PASSABLE";
    public const string Partial = "PARTIAL";
    public const string Gap = "GAP";
    public const string NotApplicable = "N/A";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Passable, Partial, Gap, NotApplicable };
}

public sealed class ManifestDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("scenarios")] public List<ManifestScenario> Scenarios { get; init; } = new();
}

public sealed class ManifestScenario
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("capability")] public string Capability { get; init; } = "";
    [JsonPropertyName("replace")] public ManifestBackend Replace { get; init; } = new();
    [JsonPropertyName("augment")] public Dictionary<string, ManifestBackend> Augment { get; init; } = new();
}

public sealed class ManifestBackend
{
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("rationale")] public string? Rationale { get; init; }
}

public sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

public static class ManifestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Load and structurally validate the scenario manifest.</summary>
    public static ManifestDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new ManifestException($"Scenario manifest not found: {path}");

        ManifestDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<ManifestDocument>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new ManifestException($"Scenario manifest is malformed JSON: {ex.Message}");
        }

        if (doc is null)
            throw new ManifestException("Scenario manifest deserialized to null.");
        if (doc.SchemaVersion <= 0)
            throw new ManifestException($"Scenario manifest schemaVersion must be a positive integer (was {doc.SchemaVersion}).");
        if (doc.Scenarios.Count == 0)
            throw new ManifestException("Scenario manifest contains no scenarios.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in doc.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(s.Id))
                throw new ManifestException("Scenario manifest contains a scenario with an empty id.");
            if (!seen.Add(s.Id))
                throw new ManifestException($"Scenario manifest contains a duplicate scenario id: {s.Id}.");
            if (!Status.All.Contains(s.Replace.Status))
                throw new ManifestException($"Scenario {s.Id} replace.status '{s.Replace.Status}' is not a valid status.");
            foreach (var (key, backend) in s.Augment)
            {
                if (!Status.All.Contains(backend.Status))
                    throw new ManifestException($"Scenario {s.Id} augment.{key}.status '{backend.Status}' is not a valid status.");
            }
        }

        return doc;
    }
}
