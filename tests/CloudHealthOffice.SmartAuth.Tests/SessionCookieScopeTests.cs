using FluentAssertions;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// The sign-in session cookie belongs to auth.cloudhealthoffice.com alone: it
/// is host-only (no Domain attribute) and nothing in front of the service
/// rewrites it onto every *.cloudhealthoffice.com host.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SessionCookieScopeTests
{
    private readonly SmartAuthTestFixture _fixture;

    public SessionCookieScopeTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SessionCookie_IsHostOnly_HttpOnly_AndLax()
    {
        var browser = new SmartAuthDriver(_fixture.Factory).Anonymous();
        var resp = await browser.PostAsync("/account/login?returnUrl=%2F", new FormUrlEncodedContent(
        [
            new("username", "cookie-scope-" + Guid.NewGuid().ToString("N")[..6]),
            new("password", "Password123!"),
        ]));

        var session = resp.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal));
        var attributes = session.Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant()).ToList();
        attributes.Should().NotContain(a => a.StartsWith("domain="), "the session cookie is host-only");
        attributes.Should().Contain("httponly");
        attributes.Should().Contain("samesite=lax");
    }

    [Fact]
    public void Ingress_DoesNotRewriteCookieDomainOrPath()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "src/services/smart-auth-service/k8s/smart-auth-service-deployment.yaml"));

        manifest.Should().NotContain("proxy-cookie-domain", "rewriting the domain would widen the session to every subdomain");
        manifest.Should().NotContain("proxy-cookie-path");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "services", "smart-auth-service")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
