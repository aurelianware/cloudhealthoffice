using System.Text.Json;
using FhirService.Models;

namespace FhirService.Services;

/// <summary>
/// Reads the prefetch an EHR sends with a CDS Hooks request.
///
/// <para>
/// CHO has declared <c>Coverage?patient={{context.patientId}}&amp;status=active</c>
/// in its discovery document since CRD was added, and never read it (gap-matrix
/// G-46). It needs it now: the <c>coverage-information</c> extension requires a
/// Coverage reference, and the prefetch is where a conforming EHR has already
/// put one.
/// </para>
///
/// <para>
/// Nothing here falls back to a placeholder. A coverage assertion that names a
/// Coverage CHO did not actually resolve would be a fabricated reference in a
/// resource the EHR is being asked to store, which is worse than the card-only
/// answer this replaces. When the prefetch carries no usable Coverage, the
/// caller emits no system action and the cards still answer.
/// </para>
/// </summary>
public static class CrdPrefetch
{
    /// <summary>The prefetch key CHO's discovery document declares for coverage.</summary>
    public const string CoverageKey = "coverage";

    /// <summary>
    /// The reference (<c>Coverage/{id}</c>) of the first active Coverage in the
    /// prefetch, or null when there is none to read.
    ///
    /// <para>
    /// An EHR may answer a search-shaped prefetch template with a Bundle, or a
    /// single resource where it resolved one; both are accepted. A Coverage
    /// whose <c>status</c> is present and is not <c>active</c> is skipped — the
    /// template asked for active coverage, and a server that answered with
    /// something else has not given CHO what it asked for. A Coverage with no
    /// status at all is accepted: absent is not the same as contradicted.
    /// </para>
    /// </summary>
    public static string? ActiveCoverageReference(CrdHookRequest request)
    {
        if (request.Prefetch is null
            || !request.Prefetch.TryGetValue(CoverageKey, out var prefetched)
            || prefetched.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var candidate in CoverageResources(prefetched))
        {
            if (Reference(candidate) is { } reference)
            {
                return reference;
            }
        }

        return null;
    }

    /// <summary>Yields the Coverage resources in a prefetch value, Bundle or bare resource.</summary>
    private static IEnumerable<JsonElement> CoverageResources(JsonElement prefetched)
    {
        if (ResourceType(prefetched) == "Coverage")
        {
            yield return prefetched;
            yield break;
        }

        if (ResourceType(prefetched) != "Bundle"
            || !prefetched.TryGetProperty("entry", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("resource", out var resource)
                && ResourceType(resource) == "Coverage")
            {
                yield return resource;
            }
        }
    }

    private static string? Reference(JsonElement coverage)
    {
        if (String(coverage, "status") is { } status
            && !string.Equals(status, "active", StringComparison.Ordinal))
        {
            return null;
        }

        return String(coverage, "id") is { Length: > 0 } id ? $"Coverage/{id}" : null;
    }

    private static string? ResourceType(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? String(element, "resourceType") : null;

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
