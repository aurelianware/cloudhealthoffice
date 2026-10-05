using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// No code and no refresh token reach an interactive app until the signed-in
/// person has approved that app for that scope set on the consent page. The
/// approval is remembered per (identity, client, scopes).
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartConsentTests
{
    private const string Scope = "openid offline_access launch/patient patient/*.read";

    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;

    public SmartConsentTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
    }

    private async Task<(string Tenant, string App, HttpClient Member)> MemberAndAppAsync()
    {
        var tenant = SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app",
            "openid", "offline_access", "launch/patient", "patient/*.read", "patient/Coverage.read");
        var member = await _smart.LinkedMemberAsync(tenant, "mbr-" + Guid.NewGuid().ToString("N")[..6]);
        return (tenant, app, member);
    }

    private static string AuthorizeUrl(string app, string scope, IDictionary<string, string?>? extra = null)
    {
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes("v".PadRight(43, 'v'))))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = app, ["redirect_uri"] = SmartAuthDriver.RedirectUri,
            ["scope"] = scope, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = "s",
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string?>()) query[k] = v;
        return QueryHelpers.AddQueryString("/connect/authorize", query);
    }

    [Fact]
    public async Task FirstAuthorization_ShowsTheConsentPage_WithTheClientAndScopes_AndIssuesNoCode()
    {
        var (_, app, member) = await MemberAndAppAsync();

        var resp = await member.GetAsync(AuthorizeUrl(app, Scope));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the consent page, not a redirect with a code");
        resp.Headers.Location.Should().BeNull();
        var page = await resp.Content.ReadAsStringAsync();
        page.Should().Contain($"<strong id=\"client-name\">{app}</strong>");
        page.Should().Contain("Read all patient-level resources");
        page.Should().Contain("refresh token", "offline_access is spelled out");
        page.Should().Contain("name=\"consent_decision\" value=\"approve\"");
        page.Should().Contain("name=\"consent_decision\" value=\"deny\"");
    }

    [Fact]
    public async Task Deny_SendsAccessDenied_AndRemembersNothing()
    {
        var (_, app, member) = await MemberAndAppAsync();

        var denied = await SmartAuthDriver.AuthorizeAsync(member, app, Scope, consent: "deny");
        denied.Code.Should().BeNull();
        denied.Error.Should().Be("access_denied");

        (await member.GetAsync(AuthorizeUrl(app, Scope))).StatusCode.Should().Be(HttpStatusCode.OK, "still asks");
    }

    [Fact]
    public async Task Approval_IsRemembered_ForThatScopeSet_AndAskedAgainForMore()
    {
        var (_, app, member) = await MemberAndAppAsync();

        (await SmartAuthDriver.AuthorizeAsync(member, app, Scope)).Code.Should().NotBeNull();

        // Same client and scopes: straight back to the app with a code.
        var again = await member.GetAsync(AuthorizeUrl(app, Scope));
        again.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(again.Headers.Location!.Query).Should().ContainKey("code");

        // A scope not yet approved: the page again.
        (await member.GetAsync(AuthorizeUrl(app, Scope + " patient/Coverage.read")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApprovalWithoutAValidConsentToken_IsNotHonoured()
    {
        var (_, app, member) = await MemberAndAppAsync();
        var page = await (await member.GetAsync(AuthorizeUrl(app, Scope))).Content.ReadAsStringAsync();
        var form = SmartAuthDriver.ConsentForm(page);

        // No token, a garbage token, and a valid token for a narrower scope set
        // posted with wider scopes: each gets the consent page again, no code.
        var noToken = form.Where(p => p.Key != "consent_token").Append(new("consent_decision", "approve")).ToList();
        var garbage = form.Select(p => p.Key == "consent_token" ? new KeyValuePair<string, string>(p.Key, "x") : p)
            .Append(new("consent_decision", "approve")).ToList();
        var widened = form.Select(p => p.Key == "scope" ? new KeyValuePair<string, string>(p.Key, Scope + " patient/Coverage.read") : p)
            .Append(new("consent_decision", "approve")).ToList();

        foreach (var attempt in new[] { noToken, garbage, widened })
        {
            var resp = await member.PostAsync("/connect/authorize", new FormUrlEncodedContent(attempt));
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            resp.Headers.Location.Should().BeNull();
        }
    }

    [Fact]
    public async Task ConsentTokenOfAnotherPerson_IsNotHonoured()
    {
        var (tenant, app, member) = await MemberAndAppAsync();
        var other = await _smart.LinkedMemberAsync(tenant, "mbr-other-" + Guid.NewGuid().ToString("N")[..4]);

        // The attacker's own consent page, posted from the victim's browser.
        var attackerForm = SmartAuthDriver.ConsentForm(
            await (await other.GetAsync(AuthorizeUrl(app, Scope))).Content.ReadAsStringAsync());
        attackerForm.Add(new("consent_decision", "approve"));

        var resp = await member.PostAsync("/connect/authorize", new FormUrlEncodedContent(attackerForm));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the victim is asked; nothing is approved for them");
    }

    [Fact]
    public async Task PromptNone_WithoutConsent_IsConsentRequired()
    {
        var (_, app, member) = await MemberAndAppAsync();

        var resp = await member.GetAsync(AuthorizeUrl(app, Scope, new Dictionary<string, string?> { ["prompt"] = "none" }));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(resp.Headers.Location!.Query)["error"].ToString().Should().Be("consent_required");
    }

    [Fact]
    public async Task RefreshToken_IsTiedToTheApproval_RevokingItEndsRefresh()
    {
        var (_, app, member) = await MemberAndAppAsync();
        var outcome = await SmartAuthDriver.AuthorizeAsync(member, app, Scope);
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var refresh = body.GetProperty("refresh_token").GetString()!;

        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var appId = await applications.GetIdAsync((await applications.FindByClientIdAsync(app))!);
            await foreach (var authorization in authorizations.FindByApplicationIdAsync(appId!))
                await authorizations.TryRevokeAsync(authorization);
        }

        var (refreshStatus, _) = await _smart.RefreshAsync(app, refresh);
        refreshStatus.Should().Be(HttpStatusCode.BadRequest, "the approval the refresh token rests on is revoked");
    }
}
