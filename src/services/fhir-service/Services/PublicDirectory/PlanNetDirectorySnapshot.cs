using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// One published directory, held whole: every Plan-Net resource it serves,
/// indexed by type and id. Built by <see cref="PlanNetDirectorySource"/>,
/// cached by <see cref="PlanNetDirectoryStore"/> and searched by
/// <see cref="PlanNetSearchEngine"/>. Never changed once built: a refresh
/// builds a new snapshot and swaps it in, so a search always sees one
/// consistent directory.
///
/// Each resource gets <c>meta.versionId</c>, a hash of its content, so a
/// version names exactly one content and vread of the current version works.
/// </summary>
public sealed class PlanNetDirectorySnapshot
{
    private static readonly IReadOnlyList<JsonObject> None = Array.Empty<JsonObject>();

    private readonly Dictionary<string, List<JsonObject>> _byType;
    private readonly Dictionary<string, JsonObject> _byReference;

    private PlanNetDirectorySnapshot(
        string directoryId,
        DateTimeOffset builtAt,
        Dictionary<string, List<JsonObject>> byType,
        Dictionary<string, JsonObject> byReference)
    {
        DirectoryId = directoryId;
        BuiltAt = builtAt;
        _byType = byType;
        _byReference = byReference;
    }

    public string DirectoryId { get; }

    public DateTimeOffset BuiltAt { get; }

    public int Count => _byReference.Count;

    /// <summary>All resources of <paramref name="resourceType"/>, ordered by id.</summary>
    public IReadOnlyList<JsonObject> OfType(string resourceType)
        => _byType.TryGetValue(resourceType, out var list) ? list : None;

    public JsonObject? Get(string resourceType, string id)
        => _byReference.TryGetValue($"{resourceType}/{id}", out var resource) ? resource : null;

    /// <summary>The resource a literal reference (<c>Type/id</c>) names, or null.</summary>
    public JsonObject? Resolve(string reference)
        => _byReference.TryGetValue(reference, out var resource) ? resource : null;

    public static string Key(JsonObject resource)
        => $"{resource["resourceType"]!.GetValue<string>()}/{resource["id"]!.GetValue<string>()}";

    /// <summary>
    /// A snapshot of <paramref name="resources"/>. Resources without a type or
    /// id are dropped; a later duplicate of the same type and id replaces an
    /// earlier one.
    /// </summary>
    public static PlanNetDirectorySnapshot Create(
        string directoryId, IEnumerable<JsonObject> resources, DateTimeOffset builtAt)
    {
        var byReference = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (resource["resourceType"] is not JsonValue type || !type.TryGetValue<string>(out var t) || string.IsNullOrEmpty(t))
                continue;
            if (resource["id"] is not JsonValue id || !id.TryGetValue<string>(out var i) || string.IsNullOrEmpty(i))
                continue;
            Materialize(resource);
            StampVersion(resource);
            byReference[$"{t}/{i}"] = resource;
        }

        var byType = byReference.Values
            .GroupBy(r => r["resourceType"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(r => r["id"]!.GetValue<string>(), StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

        return new PlanNetDirectorySnapshot(directoryId, builtAt, byType, byReference);
    }

    /// <summary>
    /// A parsed JsonNode builds its children on first access, which is not
    /// thread-safe. Searches read a snapshot from many requests at once, so
    /// every node is built here, while only one thread can see it.
    /// </summary>
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj) Materialize(child);
                break;
            case JsonArray array:
                foreach (var child in array) Materialize(child);
                break;
        }
    }

    private static void StampVersion(JsonObject resource)
    {
        var meta = resource["meta"] as JsonObject;
        meta?.Remove("versionId");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(resource.ToJsonString()));
        if (meta is null)
        {
            meta = new JsonObject();
            resource["meta"] = meta;
        }
        meta["versionId"] = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
