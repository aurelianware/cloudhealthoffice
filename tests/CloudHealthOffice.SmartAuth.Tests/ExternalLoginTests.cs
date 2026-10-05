using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Server.AspNetCore;
using SmartAuthService.Services;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// The production sign-in: smart-auth-service as a relying party of an
/// external OIDC provider (Entra External ID), driven end to end against an
/// in-process fake IdP.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class ExternalLoginTests
{
    private const string ExpectedRedirectUri = "https://auth.cloudhealthoffice.com/signin-oidc";

    private readonly SmartAuthTestFixture _fixture;

    public ExternalLoginTests(SmartAuthTestFixture fixture) => _fixture = fixture;

    private static HttpClient Browser(WebApplicationFactory<SmartAuthService.Program> host, string baseAddress = "http://localhost")
        => host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri(baseAddress),
        });

    // ── The challenge ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Challenge_IsCodeFlowWithPkceAndNonce_AndTheRedirectUriComesFromConfiguration()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);

        var challenge = await FakeExternalIdp.StartAsync(Browser(host));

        challenge.Query["client_id"].Should().Be(FakeExternalIdp.ClientId);
        challenge.Query["response_type"].Should().Be("code");
        challenge.Query["code_challenge_method"].Should().Be("S256");
        challenge.Query["code_challenge"].Should().NotBeNullOrEmpty();
        challenge.Query["nonce"].Should().NotBeNullOrEmpty();
        challenge.Query["scope"].Split(' ').Should().BeEquivalentTo("openid", "profile");
        challenge.Query.Should().NotContainKey("response_mode", "query is the default for the code flow");
        challenge.RedirectUri.Should().Be(ExpectedRedirectUri,
            "the redirect URI is {SmartAuth:Issuer}/signin-oidc, not the request's Host (localhost)");
    }

    [Fact]
    public async Task Challenge_SpoofedHostOrForwardedHeaders_DoNotChangeTheRedirectUri()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var browser = Browser(host);
        browser.DefaultRequestHeaders.Host = "evil.example";
        browser.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");
        browser.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        (await FakeExternalIdp.StartAsync(browser)).RedirectUri.Should().Be(ExpectedRedirectUri);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("~//evil.example/steal")]
    [InlineData("~/\\evil.example")]
    [InlineData("~/\t/evil.example")]
    public async Task ExternalLogin_NeverRedirectsOffSite_AfterSignIn(string returnUrl)
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);

        var callback = await idp.SignInAsync(Browser(host), returnUrl: returnUrl);

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().Be("/");
    }

    [Fact]
    public async Task ExternalLogin_AppRelativeReturnUrl_IsFollowedAsALocalPath()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);

        var callback = await idp.SignInAsync(Browser(host), returnUrl: "~/account/link");

        callback.Headers.Location!.ToString().Should().Be("/account/link");
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/connect/authorize?x=1", "/connect/authorize?x=1")]
    [InlineData("~/ok", "/ok")]
    [InlineData("~/", "/")]
    [InlineData("/%2F%2Fevil.example", "/%2F%2Fevil.example")] // a path; browsers do not decode %2F in Location
    [InlineData("//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("~//evil.example", null)]
    [InlineData("~/\\evil.example", null)]
    [InlineData("~", null)]
    [InlineData("~evil", null)]
    [InlineData("https://evil.example", null)]
    [InlineData("/\t/evil.example", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void LocalUrl_Normalize_KeepsOnlySingleSlashLocalPaths(string? url, string? expected)
    {
        LocalUrl.Normalize(url).Should().Be(expected);
        LocalUrl.IsLocal(url).Should().Be(expected is not null);
    }

    // ── The session ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_IsExactlyIssuerAndOid_EveryOtherIdpClaimIsDropped()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var oid = Guid.NewGuid().ToString();

        var callback = await idp.SignInAsync(Browser(host), t =>
        {
            t.Claims["oid"] = oid;
            t.Claims["sub"] = "pairwise-subject";
            t.Claims["tid"] = "some-entra-tenant";
            t.Claims["tenant_id"] = "victim-tenant";
            t.Claims["roles"] = new[] { "TenantAdmin" };
            t.Claims["role"] = "admin";
            t.Claims["email"] = "someone@example.com";
            t.Claims["preferred_username"] = "someone@example.com";
            t.Claims["patient"] = "pat-001";
            t.Claims["memberId"] = "pat-001";
            t.Claims["extension_TenantId"] = "victim-tenant";
            t.Claims["cho_idp_iss"] = "urn:forged";
        });

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.ToString().Should().Be("/account/link");
        var ticket = FakeExternalIdp.SessionOf(host, callback);
        ticket.Should().NotBeNull("a valid ID token signs the person in");

        ticket!.Principal.Claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(new[]
        {
            (SmartSession.IssuerClaim, FakeExternalIdp.Issuer),
            (ClaimTypes.NameIdentifier, oid),
        });
        ticket.Principal.Identities.Should().ContainSingle();
        ticket.Properties.Items.Keys.Should().NotContain(k => k.Contains("token", StringComparison.OrdinalIgnoreCase),
            "IdP tokens are not kept in the session");
    }

    [Fact]
    public async Task Session_UsesSub_OnlyWhenTheTokenHasNoOid_AndKeepsTheNameForDisplay()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);

        var callback = await idp.SignInAsync(Browser(host), t =>
        {
            t.Claims.Remove("oid");
            t.Claims["sub"] = "only-a-sub";
            t.Claims["name"] = "Pat Member";
            t.Claims["email"] = "pat@example.com";
        });

        FakeExternalIdp.SessionOf(host, callback)!.Principal.Claims.Select(c => (c.Type, c.Value)).Should().BeEquivalentTo(new[]
        {
            (SmartSession.IssuerClaim, FakeExternalIdp.Issuer),
            (ClaimTypes.NameIdentifier, "only-a-sub"),
            (ClaimTypes.Name, "Pat Member"),
        });
    }

    public static TheoryData<string, Action<FakeExternalIdp, FakeExternalIdp.IdToken>> InvalidTokens => new()
    {
        { "wrong iss", (_, t) => t.Issuer = "https://attacker.example/v2.0" },
        { "iss differs only by a trailing slash", (_, t) => t.Issuer = FakeExternalIdp.Issuer + "/" },
        { "wrong aud", (_, t) => t.Audience = "some-other-app" },
        { "bad signature", (idp, t) => t.SignWith = idp.ForgedKey() },
        { "expired", (_, t) => t.IssuedAt = DateTime.UtcNow.AddHours(-3) },
        { "wrong nonce", (_, t) => t.Nonce = "a-nonce-from-another-sign-in" },
        { "no nonce", (_, t) => t.Nonce = null },
    };

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task InvalidIdToken_IsRefused_AndNoSessionIsCreated(
        string why, Action<FakeExternalIdp, FakeExternalIdp.IdToken> tamper)
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var browser = Browser(host);

        var callback = await idp.SignInAsync(browser, t => tamper(idp, t));

        callback.StatusCode.Should().Be(HttpStatusCode.Redirect, why);
        callback.Headers.Location!.ToString().Should().StartWith("/account/login?error=external", why);
        FakeExternalIdp.SessionCookie(callback).Should().BeNull(why);
        (await browser.GetAsync("/account/link")).Headers.Location!.ToString()
            .Should().StartWith("/account/login", $"{why}: the browser is not signed in");
    }

    [Fact]
    public async Task DiscoveryAdvertisingAnotherIssuer_DoesNotWidenTheExpectedIssuer()
    {
        // The OIDC handler adds the discovery document's issuer to the valid
        // issuers. A token from that issuer must still be refused: only
        // SmartAuth:ExternalLogin:ExpectedIssuer is accepted.
        const string other = "https://other-tenant.fake-ciam.test/v2.0";
        var idp = new FakeExternalIdp { DiscoveryIssuer = other };
        await using var host = idp.Host(_fixture.Factory);

        var callback = await idp.SignInAsync(Browser(host), t => t.Issuer = other);

        callback.Headers.Location!.ToString().Should().StartWith("/account/login?error=external");
        FakeExternalIdp.SessionCookie(callback).Should().BeNull();
    }

    [Fact]
    public async Task ReplayedCallback_IsRefused()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var browser = Browser(host);
        var challenge = await FakeExternalIdp.StartAsync(browser);
        (await idp.CompleteAsync(browser, challenge)).Headers.Location!.ToString().Should().Be("/account/link");

        // Same state again (correlation cookie already consumed) on a fresh browser.
        var replay = await idp.CompleteAsync(Browser(host), challenge);
        replay.Headers.Location!.ToString().Should().StartWith("/account/login?error=external");
        FakeExternalIdp.SessionCookie(replay).Should().BeNull();
    }

    [Fact]
    public async Task CodeRedemption_SendsTheConfiguredRedirectUri_ThePkceVerifier_AndTheClientSecret()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);

        (await idp.SignInAsync(Browser(host))).Headers.Location!.ToString().Should().Be("/account/link");

        var form = idp.TokenRequests.Should().ContainSingle().Subject;
        form["redirect_uri"].Should().Be(ExpectedRedirectUri);
        form["code_verifier"].Should().NotBeNullOrEmpty();
        form["client_secret"].Should().Be(FakeExternalIdp.ClientSecret);
    }

    // ── End to end: identity → enrolment → SMART token ────────────────────────

    [Fact]
    public async Task ExternalIdentity_GetsNoToken_UntilItRedeemsACode_ThenTheBindingDecidesTenantAndPatient()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var smart = new SmartAuthDriver(host);
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var browser = Browser(host);
        var oid = Guid.NewGuid().ToString();

        (await idp.SignInAsync(browser, t =>
        {
            t.Claims["oid"] = oid;
            t.Claims["tenant_id"] = tenant;        // ignored
            t.Claims["patient"] = "pat-777";       // ignored
        })).Headers.Location!.ToString().Should().Be("/account/link");

        // Signed in but unlinked: no token, whatever the IdP said.
        var refused = await SmartAuthDriver.AuthorizeAsync(browser, app, "openid launch/patient patient/*.read");
        refused.Code.Should().BeNull();
        refused.Error.Should().Be("access_denied");

        // The enrolment code binds THIS (issuer, oid).
        var code = await smart.IssueMemberCodeAsync(tenant, "mbr-ext-1");
        (await SmartAuthDriver.RedeemAsync(browser, code)).StatusCode.Should().Be(HttpStatusCode.OK);

        var token = SmartAuthDriver.Read(await smart.MemberAccessTokenAsync(browser, app));
        SmartAuthDriver.Claim(token, "tenant_id").Should().Be(tenant);
        SmartAuthDriver.Claim(token, "patient").Should().Be("mbr-ext-1");
        SmartAuthDriver.Claim(token, "sub").Should().Be(oid);

        // Another person at the same IdP (another oid) is not that member.
        var other = Browser(host);
        await idp.SignInAsync(other);
        (await SmartAuthDriver.AuthorizeAsync(other, app, "openid launch/patient patient/*.read")).Error.Should().Be("access_denied");
    }

    [Fact]
    public async Task AuthorizeWithoutASession_SendsTheBrowserToTheLoginPage_WhichOffersTheExternalLogin()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var smart = new SmartAuthDriver(host);
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await smart.RegisterClientAsync(tenant, "patient-app", "openid", "patient/*.read");
        var browser = Browser(host);

        var authorize = await browser.GetAsync($"/connect/authorize?response_type=code&client_id={app}"
            + $"&redirect_uri={Uri.EscapeDataString(SmartAuthDriver.RedirectUri)}&scope=openid%20patient%2F*.read"
            + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256");
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var login = authorize.Headers.Location!;
        login.AbsolutePath.Should().Be("/account/login");

        var page = await (await browser.GetAsync(login.PathAndQuery)).Content.ReadAsStringAsync();
        page.Should().Contain("Sign in with Example ID");
        page.Should().Contain("/account/external-login?returnUrl=%2Fconnect%2Fauthorize");
    }

    // ── Login page ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginPage_ShowsTheExternalButton_OnlyWhenEnabled()
    {
        var idp = new FakeExternalIdp();
        await using var enabled = idp.Host(_fixture.Factory, b => b.UseSetting("SmartAuth:ExternalLogin:DisplayName", "<Payer> ID"));

        var withExternal = await (await Browser(enabled).GetAsync("/account/login?returnUrl=%2Fx")).Content.ReadAsStringAsync();
        withExternal.Should().Contain("Sign in with &lt;Payer&gt; ID", "the display name is HTML-encoded");
        withExternal.Should().Contain("href=\"/account/external-login?returnUrl=%2Fx\"");
        withExternal.Should().Contain("name=\"password\"", "the development login stays on a Development host with DevMode");

        var without = await (await Browser(_fixture.Factory).GetAsync("/account/login")).Content.ReadAsStringAsync();
        without.Should().NotContain("external-login");
        (await Browser(_fixture.Factory).GetAsync("/account/external-login")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LoginPage_DropsANonLocalReturnUrl()
    {
        var idp = new FakeExternalIdp();
        await using var host = idp.Host(_fixture.Factory);
        var page = await (await Browser(host).GetAsync("/account/login?returnUrl=https%3A%2F%2Fevil.example")).Content.ReadAsStringAsync();
        page.Should().NotContain("evil.example");
    }

    // ── Outside Development ───────────────────────────────────────────────────

    /// <summary>A non-Development host (Testing): Secure cookies, no development login.</summary>
    private static WebApplicationFactory<SmartAuthService.Program> TestingHost(
        FakeExternalIdp? idp, bool devMode = true, string? database = null)
        => new WebApplicationFactory<SmartAuthService.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.UseSetting("SmartAuth:DevMode", devMode ? "true" : "false");
            b.UseSetting("ChoAuth:Audience", "cho-api");
            b.UseSetting("ChoAuth:Issuers:0:Issuer", "cho-portal-dev");
            b.UseSetting("ChoAuth:Issuers:0:SymmetricKey", "Q0hPLWRldmVsb3BtZW50LW9ubHktc2lnbmluZy1rZXktZG8tbm90LXVzZS0yMDI2");
            if (database != null) b.UseSetting("MongoDb:DatabaseName", database);
            idp?.Apply(b);
            b.ConfigureTestServices(services =>
                services.PostConfigure<OpenIddictServerAspNetCoreOptions>(o => o.DisableTransportSecurityRequirement = true));
        });

    [Fact]
    public async Task DevelopmentLogin_IsRefused_OnANonDevelopmentHost_EvenWithDevMode()
    {
        await using var host = TestingHost(idp: null, devMode: true);
        var browser = Browser(host, "https://localhost");

        var page = await (await browser.GetAsync("/account/login")).Content.ReadAsStringAsync();
        page.Should().Contain("No sign-in method is configured");
        page.Should().NotContain("password");

        var post = await browser.PostAsync("/account/login?returnUrl=%2Faccount%2Flink", new FormUrlEncodedContent(
        [
            new("username", "demo-member"),
            new("password", "Password123!"),
        ]));
        post.Headers.Location!.ToString().Should().Contain("error=invalid");
        FakeExternalIdp.SessionCookie(post).Should().BeNull();
    }

    [Fact]
    public async Task OutsideDevelopment_TheExternalLoginWorks_AndEveryCookieIsSecureHttpOnlyLax()
    {
        // Plain HTTP, as the pod sees it behind the TLS-terminating ingress:
        // the cookies must be Secure anyway. (The browser's own requests are
        // HTTPS, so the cookies are forwarded by hand here.)
        var idp = new FakeExternalIdp();
        await using var host = TestingHost(idp, devMode: true);
        var browser = Browser(host);

        var page = await (await browser.GetAsync("/account/login")).Content.ReadAsStringAsync();
        page.Should().Contain("Sign in with Example ID");
        page.Should().NotContain("password", "the development login is Development-only");

        var challenge = await FakeExternalIdp.StartAsync(browser);
        challenge.RedirectUri.Should().Be(ExpectedRedirectUri);
        var flowCookies = FakeExternalIdp.SetCookies(challenge.Response).ToList();
        flowCookies.Should().Contain(c => c.StartsWith(".AspNetCore.Correlation."));
        flowCookies.Should().Contain(c => c.StartsWith(".AspNetCore.OpenIdConnect.Nonce."));
        flowCookies.Should().OnlyContain(c => IsSecureHttpOnlyLax(c));

        var returning = Browser(host);
        returning.DefaultRequestHeaders.Add("Cookie", string.Join("; ", flowCookies.Select(c => c.Split(';')[0])));
        var oid = Guid.NewGuid().ToString();
        var callback = await idp.CompleteAsync(returning, challenge, t => t.Claims["oid"] = oid);
        callback.Headers.Location!.ToString().Should().Be("/account/link");
        var session = FakeExternalIdp.SetCookies(callback).Single(c => c.StartsWith(".AspNetCore.Cookies="));
        IsSecureHttpOnlyLax(session).Should().BeTrue(session);
        FakeExternalIdp.SessionOf(host, callback)!.Principal.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be(oid);

        var signedIn = Browser(host);
        signedIn.DefaultRequestHeaders.Add("Cookie", session.Split(';')[0]);
        (await signedIn.GetAsync("/account/link")).StatusCode.Should().Be(HttpStatusCode.OK, "the session is honoured");
    }

    private static bool IsSecureHttpOnlyLax(string setCookie)
    {
        var attributes = setCookie.Split(';').Select(a => a.Trim().ToLowerInvariant()).ToList();
        return attributes.Contains("secure") && attributes.Contains("httponly") && attributes.Contains("samesite=lax");
    }

    [Fact]
    public async Task ASignInStartedOnOnePod_CompletesOnAnother_AndTheSessionIsHonouredByAThird()
    {
        // Data Protection keys are shared through MongoDB, so the state,
        // correlation and nonce written by one pod are read by another.
        var idp = new FakeExternalIdp();
        var database = "dp-" + Guid.NewGuid().ToString("N")[..8];
        await using var podA = TestingHost(idp, database: database);
        await using var podB = TestingHost(idp, database: database);
        await using var podC = TestingHost(idp, database: database);

        var browserA = Browser(podA, "https://localhost");
        var challenge = await FakeExternalIdp.StartAsync(browserA);

        var browserB = Browser(podB, "https://localhost");
        var flowCookies = string.Join("; ", FakeExternalIdp.SetCookies(challenge.Response).Select(c => c.Split(';')[0]));
        browserB.DefaultRequestHeaders.Add("Cookie", flowCookies);
        var callback = await idp.CompleteAsync(browserB, challenge);
        callback.Headers.Location!.ToString().Should().Be("/account/link");

        var browserC = Browser(podC, "https://localhost");
        browserC.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Cookies=" + FakeExternalIdp.SessionCookie(callback));
        (await browserC.GetAsync("/account/link")).StatusCode.Should().Be(HttpStatusCode.OK);

        var keys = podA.Services.GetRequiredService<MongoDB.Driver.IMongoDatabase>()
            .GetCollection<MongoDB.Bson.BsonDocument>(MongoDataProtectionRepository.CollectionName);
        (await keys.CountDocumentsAsync(MongoDB.Driver.FilterDefinition<MongoDB.Bson.BsonDocument>.Empty)).Should().BeGreaterThan(0);
    }
}
