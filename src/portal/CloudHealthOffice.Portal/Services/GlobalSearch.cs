namespace CloudHealthOffice.Portal.Services;

/// <summary>One thing global search found.</summary>
/// <param name="Kind">Claim, Member or Provider.</param>
/// <param name="Exact">The query is this record's identifier (claim number, member ID, NPI).</param>
public sealed record SearchHit(string Kind, string Title, string? Subtitle, string Href, bool Exact);

/// <summary>What a query found, plus links to keep searching in each module.</summary>
public sealed class SearchResults
{
    public string Query { get; init; } = string.Empty;
    public List<SearchHit> Hits { get; } = new();
    public List<(string Label, string Href)> MoreLinks { get; } = new();
    public List<string> Unavailable { get; } = new();

    /// <summary>The one record to jump straight to, when exactly one identifier matched.</summary>
    public SearchHit? Jump => Hits.Count(h => h.Exact) == 1 ? Hits.Single(h => h.Exact) : null;
}

/// <summary>
/// The search box on every page (docs/architecture/portal-navigation.md
/// §3.2): an identifier jumps straight to its record; anything else lists
/// what matched, grouped by kind. Only modules the user may read are
/// searched, and each module's own API enforces (and logs) the read.
/// </summary>
public sealed class GlobalSearch
{
    private readonly IClaimsService _claims;
    private readonly IMemberService _members;
    private readonly IProviderService _providers;
    private readonly IUserContextService _user;

    public GlobalSearch(IClaimsService claims, IMemberService members, IProviderService providers, IUserContextService user)
    {
        _claims = claims;
        _members = members;
        _providers = providers;
        _user = user;
    }

    public const int MaxPerKind = 8;

    public async Task<SearchResults> SearchAsync(string query)
    {
        var q = query.Trim();
        var results = new SearchResults { Query = q };
        if (q.Length == 0) return results;

        try { await _user.GetCurrentUserAsync(); } catch { /* permissions fall back to none */ }
        var tasks = new List<Task>();
        if (_user.HasPermission("claims:read")) tasks.Add(Claims(q, results));
        if (_user.HasPermission("members:read")) tasks.Add(Members(q, results));
        if (_user.HasPermission("providers:read")) tasks.Add(Providers(q, results));
        await Task.WhenAll(tasks);

        // Exact matches first, then by kind in a fixed order.
        var order = new[] { "Claim", "Member", "Provider" };
        var sorted = results.Hits.OrderByDescending(h => h.Exact).ThenBy(h => Array.IndexOf(order, h.Kind)).ToList();
        results.Hits.Clear();
        results.Hits.AddRange(sorted);
        return results;
    }

    public static bool LooksLikeNpi(string q) => q.Length == 10 && q.All(char.IsDigit);

    private async Task Claims(string q, SearchResults results)
    {
        try
        {
            var found = await _claims.SearchClaimsAsync(new ClaimSearchRequest { ClaimNumber = q, PageSize = MaxPerKind });
            lock (results)
            {
                foreach (var c in (found?.Claims ?? new()).Take(MaxPerKind))
                {
                    results.Hits.Add(new SearchHit("Claim", $"Claim {c.ClaimNumber}",
                        $"{c.Status} · member {c.MemberId}", $"/claims/{Uri.EscapeDataString(c.ClaimId)}",
                        string.Equals(c.ClaimNumber, q, StringComparison.OrdinalIgnoreCase)));
                }
                results.MoreLinks.Add(("Search claims", "/claims"));
            }
        }
        catch { lock (results) results.Unavailable.Add("Claims"); }
    }

    private async Task Members(string q, SearchResults results)
    {
        try
        {
            var byId = _members.GetMemberByIdAsync(q);
            var byName = _members.SearchMembersAsync(q);
            MemberDetails? exact = null;
            try { exact = await byId; } catch { /* not an ID */ }
            var matches = await byName ?? new();
            lock (results)
            {
                if (exact is not null)
                {
                    results.Hits.Add(MemberHit(exact, exact: true));
                }
                foreach (var m in matches.Where(m => exact is null || m.MemberId != exact.MemberId).Take(MaxPerKind))
                {
                    results.Hits.Add(MemberHit(m, exact: string.Equals(m.MemberId, q, StringComparison.OrdinalIgnoreCase)));
                }
                results.MoreLinks.Add(("Search members", $"/members?memberId={Uri.EscapeDataString(q)}"));
            }
        }
        catch { lock (results) results.Unavailable.Add("Members"); }
    }

    private static SearchHit MemberHit(MemberSummary m, bool exact) =>
        new("Member", $"{m.FirstName} {m.LastName}", $"{m.MemberId} · DOB {m.DateOfBirth:MM/dd/yyyy} · {m.DisplayStatus}",
            $"/members/{Uri.EscapeDataString(m.MemberId)}", exact);

    private async Task Providers(string q, SearchResults results)
    {
        try
        {
            var found = await _providers.SearchProvidersAsync(q) ?? new();
            lock (results)
            {
                foreach (var p in found.Take(MaxPerKind))
                {
                    results.Hits.Add(new SearchHit("Provider", p.Name, $"NPI {p.NPI} · {p.Specialty} · {p.City} {p.State}".Trim(),
                        $"/providers?q={Uri.EscapeDataString(p.NPI)}", LooksLikeNpi(q) && p.NPI == q));
                }
                results.MoreLinks.Add(("Search providers", $"/providers?q={Uri.EscapeDataString(q)}"));
            }
        }
        catch { lock (results) results.Unavailable.Add("Providers"); }
    }
}
