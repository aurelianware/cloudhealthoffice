using Hl7.Fhir.Model;

namespace FhirService.Services;

/// <summary>
/// Builds FHIR R4 searchset Bundles with RFC-5988 pagination links.
/// </summary>
public class FhirBundleBuilder
{
    private readonly IConfiguration _config;

    public FhirBundleBuilder(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// Wraps a paged result set in a Bundle.  The caller provides the total count
    /// across all pages (not just the current page).
    /// </summary>
    public Bundle Build<T>(
        IReadOnlyList<T> items,
        int total,
        int page,
        int pageSize,
        string resourceType,
        string baseUrl,
        string queryString) where T : Resource
    {
        var bundle = new Bundle
        {
            Id = Guid.NewGuid().ToString("N"),
            Meta = new Meta { LastUpdated = DateTimeOffset.UtcNow },
            Type = Bundle.BundleType.Searchset,
            Total = total,
            Link = BuildLinks(resourceType, baseUrl, queryString, page, pageSize, total),
            Entry = items.Select(r => new Bundle.EntryComponent
            {
                FullUrl = $"{baseUrl}/{r.TypeName}/{r.Id}",
                Resource = r,
                Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match }
            }).ToList()
        };

        return bundle;
    }

    private static List<Bundle.LinkComponent> BuildLinks(
        string resourceType, string baseUrl, string rawQuery,
        int page, int pageSize, int total)
    {
        var links = new List<Bundle.LinkComponent>();

        var selfUrl = BuildPageUrl(baseUrl, resourceType, rawQuery, page, pageSize);
        links.Add(new Bundle.LinkComponent { Relation = "self", Url = selfUrl });

        if (page > 1)
        {
            links.Add(new Bundle.LinkComponent
            {
                Relation = "prev",
                Url = BuildPageUrl(baseUrl, resourceType, rawQuery, page - 1, pageSize)
            });
        }

        if (page * pageSize < total)
        {
            links.Add(new Bundle.LinkComponent
            {
                Relation = "next",
                Url = BuildPageUrl(baseUrl, resourceType, rawQuery, page + 1, pageSize)
            });
        }

        return links;
    }

    private static string BuildPageUrl(
        string baseUrl, string resourceType, string rawQuery, int page, int pageSize)
    {
        // Strip existing _page and _count from the original query, then append ours
        var parts = rawQuery.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("_page=", StringComparison.OrdinalIgnoreCase)
                     && !p.StartsWith("_count=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        parts.Add($"_count={pageSize}");
        parts.Add($"_page={page}");

        var qs = parts.Count > 0 ? "?" + string.Join("&", parts) : string.Empty;
        return $"{baseUrl}/{resourceType}{qs}";
    }
}
