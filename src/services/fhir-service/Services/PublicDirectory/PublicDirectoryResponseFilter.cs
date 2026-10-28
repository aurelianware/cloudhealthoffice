using System.Text.Json.Nodes;

namespace FhirService.Services.PublicDirectory;

/// <summary>
/// Cuts a directory resource down to what the public may see.
///
/// The provider-service and benefit-plan-service projections are built for
/// CHO's own callers, so they carry some things a public directory must not:
/// CHO-defined extensions such as the provider integrity score, the
/// organization's tax id, and providers who are no longer active. The public
/// route applies this to every response; the authenticated route is unchanged.
/// </summary>
public static class PublicDirectoryResponseFilter
{
    public const string ChoExtensionBase = "http://fhir.cloudhealthoffice.com/StructureDefinition/";
    public const string EinSystem = "urn:oid:2.16.840.1.113883.4.4";

    /// <summary>CHO extensions a member choosing a provider needs (accepting new patients, ages, lines of business).</summary>
    public static readonly IReadOnlySet<string> PublicChoExtensions = new HashSet<string>(StringComparer.Ordinal)
    {
        ChoExtensionBase + "practitionerrole-panel-gating",
    };

    /// <summary>
    /// The filtered body, or null when nothing of it may be shown (an inactive
    /// resource, or a body that is not a FHIR resource).
    /// </summary>
    public static string? Filter(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (System.Text.Json.JsonException) { return null; }
        if (root is not JsonObject resource || resource["resourceType"]?.GetValue<string>() is not { } type)
            return null;

        if (type == "OperationOutcome")
            return body;

        if (type == "Bundle")
        {
            if (resource["entry"] is JsonArray entries)
            {
                var removed = 0;
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i]?["resource"] is not JsonObject entryResource || !Scrub(entryResource))
                    {
                        entries.RemoveAt(i);
                        removed++;
                    }
                }
                // The upstream total counted what was just removed.
                if (removed > 0)
                    resource.Remove("total");
            }
            return resource.ToJsonString();
        }

        return Scrub(resource) ? resource.ToJsonString() : null;
    }

    /// <summary>Removes what is not public; false when the resource itself is not.</summary>
    private static bool Scrub(JsonObject resource)
    {
        if (resource["active"] is JsonValue active && active.TryGetValue<bool>(out var isActive) && !isActive)
            return false;

        if (resource["identifier"] is JsonArray identifiers)
        {
            for (var i = identifiers.Count - 1; i >= 0; i--)
            {
                if (identifiers[i]?["system"]?.GetValue<string>() == EinSystem)
                    identifiers.RemoveAt(i);
            }
            if (identifiers.Count == 0)
                resource.Remove("identifier");
        }

        StripChoExtensions(resource);
        return true;
    }

    private static void StripChoExtensions(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["extension"] is JsonArray extensions)
                {
                    for (var i = extensions.Count - 1; i >= 0; i--)
                    {
                        var url = extensions[i]?["url"]?.GetValue<string>();
                        if (url != null && url.StartsWith(ChoExtensionBase, StringComparison.Ordinal)
                            && !PublicChoExtensions.Contains(url))
                            extensions.RemoveAt(i);
                    }
                    if (extensions.Count == 0)
                        obj.Remove("extension");
                }
                foreach (var (_, child) in obj.ToList())
                    StripChoExtensions(child);
                break;
            case JsonArray array:
                foreach (var child in array)
                    StripChoExtensions(child);
                break;
        }
    }
}
