using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// The pinned external target manifest (interop/versions.json). Source-controlled
/// so a dependency change is a reviewable diff rather than a silent drift.
/// </summary>
public sealed record InteropVersions
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("lastReviewedUtc")] public string? LastReviewedUtc { get; init; }
    [JsonPropertyName("targets")] public List<ExternalServiceDefinition> Targets { get; init; } = new();
    [JsonPropertyName("contentSources")] public List<ExternalContentSource> ContentSources { get; init; } = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Loads the manifest copied next to the test assembly.</summary>
    public static InteropVersions Load() => LoadFrom(InteropPaths.VersionsManifest);

    public static InteropVersions LoadFrom(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Pinned external target manifest not found at '{path}'. It is copied next to the " +
                "test assembly from interop/versions.json by the project file.", path);
        }

        return JsonSerializer.Deserialize<InteropVersions>(File.ReadAllText(path), ReadOptions)
               ?? throw new InvalidOperationException($"'{path}' did not deserialize to a manifest.");
    }

    /// <summary>Looks up a target by its short key (e.g. "br-payer").</summary>
    public ExternalServiceDefinition Target(string key) =>
        Targets.SingleOrDefault(t => t.Key == key)
        ?? throw new KeyNotFoundException(
            $"No external target '{key}' in interop/versions.json. Known keys: " +
            string.Join(", ", Targets.Select(t => t.Key)));

    /// <summary>Looks up a pinned content source by its short key (e.g. "cds-library").</summary>
    public ExternalContentSource ContentSource(string key) =>
        ContentSources.SingleOrDefault(c => c.Key == key)
        ?? throw new KeyNotFoundException(
            $"No content source '{key}' in interop/versions.json. Known keys: " +
            string.Join(", ", ContentSources.Select(c => c.Key)));
}

/// <summary>
/// Resolves the repository paths the harness needs. Tests run from
/// bin/&lt;config&gt;/net8.0, so the repository root is found by walking up to the
/// directory that holds the interop manifest.
/// </summary>
public static class InteropPaths
{
    /// <summary>interop/versions.json, as copied beside the test assembly.</summary>
    public static string VersionsManifest =>
        Path.Combine(AppContext.BaseDirectory, "interop", "versions.json");

    /// <summary>interop/scenarios.json, as copied beside the test assembly.</summary>
    public static string ScenarioInventory =>
        Path.Combine(AppContext.BaseDirectory, "interop", "scenarios.json");

    /// <summary>
    /// The repository root, located by walking up from the test assembly until a
    /// directory containing interop/docker-compose.interop.yml is found.
    /// </summary>
    public static string RepositoryRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "interop", "docker-compose.interop.yml")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not locate the repository root: no ancestor of " +
                $"'{AppContext.BaseDirectory}' contains interop/docker-compose.interop.yml.");
        }
    }

    public static string ComposeFile =>
        Path.Combine(RepositoryRoot, "interop", "docker-compose.interop.yml");

    /// <summary>Where sanitized run evidence is written.</summary>
    public static string ArtifactsRoot =>
        Environment.GetEnvironmentVariable("CHO_INTEROP_ARTIFACTS")
        ?? Path.Combine(RepositoryRoot, "artifacts", "interop");
}
