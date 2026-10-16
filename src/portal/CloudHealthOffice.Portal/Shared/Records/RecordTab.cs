namespace CloudHealthOffice.Portal.Shared.Records;

/// <summary>
/// One tab on a record page (docs/architecture/portal-navigation.md §4).
/// The key is the last URL segment: <c>/claims/{id}/{key}</c>.
/// </summary>
public sealed record RecordTab(string Key, string Label, string? Icon = null, int? Count = null, bool Visible = true);

public static class RecordTabs
{
    /// <summary>
    /// The tab to show: the requested key if it names a visible tab,
    /// otherwise the default. An unknown or hidden tab in a URL (an old
    /// bookmark, or a tab the user may not see) falls back quietly.
    /// </summary>
    public static string Resolve(IEnumerable<RecordTab> tabs, string? requested, string fallback)
    {
        var visible = tabs.Where(t => t.Visible).ToList();
        if (requested is { Length: > 0 } && visible.FirstOrDefault(t => string.Equals(t.Key, requested, StringComparison.OrdinalIgnoreCase)) is { } match)
            return match.Key;
        return visible.Any(t => t.Key == fallback) ? fallback : visible.FirstOrDefault()?.Key ?? fallback;
    }

    /// <summary>The URL of a tab: the record's base path plus the tab key, keeping the query string.</summary>
    public static string Href(string baseHref, string key, string? query = null) =>
        $"{baseHref.TrimEnd('/')}/{Uri.EscapeDataString(key)}{(string.IsNullOrEmpty(query) ? string.Empty : query.StartsWith('?') ? query : "?" + query)}";
}
