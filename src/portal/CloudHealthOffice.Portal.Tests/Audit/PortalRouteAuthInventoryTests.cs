using System.Text.RegularExpressions;

namespace CloudHealthOffice.Portal.Tests.Audit;

/// <summary>
/// Repeatable inventory of portal <c>@page</c> routes and their auth attributes.
/// Fails when a new page is added without an explicit <c>[Authorize]</c> or
/// <c>[AllowAnonymous]</c> decision, or when a new anonymous route is introduced
/// without being added to the documented public allow-list.
/// </summary>
public class PortalRouteAuthInventoryTests
{
    private static readonly Regex PageDirective = new(@"@page\s+""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex AuthorizeAttr = new(@"\[Authorize\]|\[Microsoft\.AspNetCore\.Authorization\.Authorize\]", RegexOptions.Compiled);

    /// <summary>
    /// Public marketing, demo, legal, and auth-flow routes that currently declare
    /// <c>[AllowAnonymous]</c>. Keep this list short and review it in the portal
    /// UX/security audit when adding a new anonymous page.
    /// </summary>
    private static readonly HashSet<string> DocumentedAnonymousRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/welcome",
        "/apis",
        "/fhir-apis",
        "/api-docs",
        "/demo",
        "/demo/claims",
        "/demo/claims/{ClaimId}",
        "/demo/members",
        "/demo/eligibility",
        "/demo/authorizations",
        "/signup",
        "/login",
        "/signin",
        "/error",
        "/quickstarts/local-claims",
    };

    /// <summary>
    /// Router-only pages with no standalone UI contract. <c>Index</c> redirects
    /// based on authentication state; it is not an anonymous data page.
    /// </summary>
    private static readonly HashSet<string> RouterOnlyRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
    };

    /// <summary>
    /// Known gap: these <c>@page</c> routes currently declare neither
    /// <c>[Authorize]</c> nor <c>[AllowAnonymous]</c>. Because the portal has no
    /// fallback authorization policy, they are reachable without sign-in.
    /// Tracked as P0/P1 in <c>docs/audits/portal-ux-security-audit.md</c>. Shrink
    /// this list only by adding an explicit attribute — do not add new routes here.
    /// </summary>
    private static readonly HashSet<string> KnownUndeclaredRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/claims/submit",
        "/providers/verification",
        "/docs",
        "/pricing",
        "/contact-sales",
        "/legal",
        "/request-access",
        "/Error/AdminConsentRequired",
    };

    [Fact]
    public void UndeclaredPageRoutes_MatchKnownGapList()
    {
        var pages = LoadPages();
        pages.Should().NotBeEmpty("portal Pages directory should contain Razor pages");

        var undeclared = pages
            .Where(p => !p.Authorize && !p.AllowAnonymous)
            .SelectMany(p => p.Routes)
            .Where(route => !RouterOnlyRoutes.Contains(route))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extra = undeclared.Except(KnownUndeclaredRoutes, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var missing = KnownUndeclaredRoutes.Except(undeclared, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        extra.Should().BeEmpty(
            "new @page routes must declare [Authorize] or [AllowAnonymous]; do not expand KnownUndeclaredRoutes. Extra: {0}",
            string.Join(", ", extra));
        missing.Should().BeEmpty(
            "KnownUndeclaredRoutes is stale; remove routes that now declare an auth attribute: {0}",
            string.Join(", ", missing));
    }

    [Fact]
    public void AnonymousRoutes_MustBeOnDocumentedAllowList()
    {
        var pages = LoadPages();
        var anonymousRoutes = pages
            .Where(p => p.AllowAnonymous)
            .SelectMany(p => p.Routes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r)
            .ToList();

        var unexpected = anonymousRoutes
            .Where(r => !DocumentedAnonymousRoutes.Contains(r))
            .ToList();

        unexpected.Should().BeEmpty(
            "new [AllowAnonymous] portal routes must be added to DocumentedAnonymousRoutes and reviewed in docs/audits/portal-ux-security-audit.md. Unexpected: {0}",
            string.Join(", ", unexpected));
    }

    [Fact]
    public void DocumentedAnonymousAllowList_DoesNotContainStaleRoutes()
    {
        var pages = LoadPages();
        var actualAnonymous = pages
            .Where(p => p.AllowAnonymous)
            .SelectMany(p => p.Routes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = DocumentedAnonymousRoutes
            .Where(r => !actualAnonymous.Contains(r))
            .ToList();

        stale.Should().BeEmpty(
            "DocumentedAnonymousRoutes contains routes that are no longer [AllowAnonymous]: {0}",
            string.Join(", ", stale));
    }

    [Fact]
    public void OperationalClaimAndMemberRoutes_RequireAuthorize()
    {
        var pages = LoadPages();
        var required = new[] { "/claims", "/claims/{ClaimId}", "/members", "/work-queues", "/dashboard" };

        foreach (var route in required)
        {
            var page = pages.FirstOrDefault(p => p.Routes.Contains(route, StringComparer.OrdinalIgnoreCase));
            page.Should().NotBeNull($"expected a page for {route}");
            page!.Authorize.Should().BeTrue($"{route} must be [Authorize]");
            page.AllowAnonymous.Should().BeFalse($"{route} must not be [AllowAnonymous]");
        }
    }

    private static List<PageInventory> LoadPages()
    {
        var pagesDir = FindPagesDirectory();
        var results = new List<PageInventory>();
        foreach (var file in Directory.EnumerateFiles(pagesDir, "*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var routes = PageDirective.Matches(text).Select(m => m.Groups[1].Value).ToList();
            if (routes.Count == 0)
            {
                continue;
            }

            results.Add(new PageInventory(
                File: Path.GetRelativePath(pagesDir, file),
                Routes: routes,
                Authorize: AuthorizeAttr.IsMatch(text),
                AllowAnonymous: text.Contains("[AllowAnonymous]", StringComparison.Ordinal)));
        }

        return results;
    }

    private static string FindPagesDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "CloudHealthOffice.Portal", "Pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(current.FullName, "src", "portal", "CloudHealthOffice.Portal", "Pages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate CloudHealthOffice.Portal/Pages from the test output directory.");
    }

    private sealed record PageInventory(string File, List<string> Routes, bool Authorize, bool AllowAnonymous);
}
