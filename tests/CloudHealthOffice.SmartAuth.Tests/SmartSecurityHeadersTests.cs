using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// No other site can frame the login, consent or link pages (clickjacking an
/// Allow), and no response leaks a code or launch token through Referer.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartSecurityHeadersTests
{
    private readonly SmartAuthDriver _smart;

    public SmartSecurityHeadersTests(SmartAuthTestFixture fixture) => _smart = new SmartAuthDriver(fixture.Factory);

    private static void ShouldNotBeFramable(HttpResponseMessage resp)
    {
        resp.Headers.GetValues("X-Frame-Options").Should().ContainSingle().Which.Should().Be("DENY");
        resp.Headers.GetValues("Content-Security-Policy").Should().ContainSingle()
            .Which.Should().Contain("frame-ancestors 'none'");
        resp.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        resp.Headers.GetValues("Referrer-Policy").Should().ContainSingle().Which.Should().Be("no-referrer");
    }

    private static void ShouldHaveThePagePolicy(HttpResponseMessage resp)
    {
        ShouldNotBeFramable(resp);
        var csp = resp.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'").And.Contain("script-src 'none'").And.Contain("base-uri 'none'");
        // form-action would apply to the redirect to the client's redirect URI and break the code flow.
        csp.Should().NotContain("form-action");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task LoginPage_CannotBeFramed_AndHasThePagePolicy()
    {
        var resp = await _smart.Anonymous().GetAsync("/account/login");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveThePagePolicy(resp);
    }

    [Fact]
    public async Task LinkPage_CannotBeFramed_AndHasThePagePolicy()
    {
        var browser = await _smart.SignInAsync("hdr-" + Guid.NewGuid().ToString("N")[..8]);

        var resp = await browser.GetAsync("/account/link");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldHaveThePagePolicy(resp);
    }

    [Fact]
    public async Task ConsentPage_CannotBeFramed_AndTheApprovalStillRedirectsToTheClient()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app", "openid", "launch/patient", "patient/*.read");
        var member = await _smart.LinkedMemberAsync(tenant, "mbr-" + Guid.NewGuid().ToString("N")[..6]);

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = app, ["redirect_uri"] = SmartAuthDriver.RedirectUri,
            ["scope"] = "openid launch/patient patient/*.read", ["state"] = "s",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", ["code_challenge_method"] = "S256",
        };
        var page = await member.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", query));

        page.StatusCode.Should().Be(HttpStatusCode.OK, "the consent page");
        ShouldHaveThePagePolicy(page);

        // Approving: the redirect back to the client carries the code, and the
        // same headers (no Referer leaves with it).
        var form = SmartAuthDriver.ConsentForm(await page.Content.ReadAsStringAsync());
        form.Add(new("consent_decision", "approve"));
        var approved = await member.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        approved.Headers.Location!.ToString().Should().StartWith(SmartAuthDriver.RedirectUri);
        ShouldNotBeFramable(approved);
    }

    [Fact]
    public async Task EveryOtherResponse_ForbidsFramingToo()
    {
        var anonymous = _smart.Anonymous();

        ShouldNotBeFramable(await anonymous.GetAsync("/.well-known/openid-configuration"));
        ShouldNotBeFramable(await anonymous.GetAsync("/api/admin/smart/clients"));   // 401
        ShouldNotBeFramable(await anonymous.GetAsync("/connect/authorize?client_id=x")); // challenge / error
    }
}
