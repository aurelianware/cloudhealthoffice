using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// An approval can be withdrawn by the person (<c>/account/apps</c>) and by a
/// tenant administrator (admin API); either ends the app's refresh tokens at
/// once. A refresh token not resting on an approval (issued before consent
/// existed) is refused.
/// </summary>
[Collection(SmartAuthCollection.Name)]
public class SmartConsentRevocationTests
{
    private const string Scope = "openid offline_access launch/patient patient/*.read";

    private readonly SmartAuthTestFixture _fixture;
    private readonly SmartAuthDriver _smart;

    public SmartConsentRevocationTests(SmartAuthTestFixture fixture)
    {
        _fixture = fixture;
        _smart = new SmartAuthDriver(fixture.Factory);
    }

    private sealed record Setup(string Tenant, string App, HttpClient Member, string Refresh);

    /// <summary>A member who approved an app and holds a working refresh token for it.</summary>
    private async Task<Setup> ApprovedAppAsync(string? tenant = null)
    {
        tenant ??= SmartAuthDriver.NewTenant();
        var (app, _) = await _smart.RegisterClientAsync(tenant, "patient-app",
            "openid", "offline_access", "launch/patient", "patient/*.read");
        var member = await _smart.LinkedMemberAsync(tenant, "mbr-" + Guid.NewGuid().ToString("N")[..6]);
        var outcome = await SmartAuthDriver.AuthorizeAsync(member, app, Scope);
        var (status, body) = await _smart.ExchangeCodeAsync(app, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return new Setup(tenant, app, member, body.GetProperty("refresh_token").GetString()!);
    }

    private static List<KeyValuePair<string, string>> RevokeForm(string page, string app)
    {
        // The <li> of that app holds its form.
        var item = Regex.Matches(page, "<li[^>]*>.*?</li>", RegexOptions.Singleline)
            .Select(m => m.Value).Single(li => li.Contains($"<code style=\"color:#666\">{app}</code>"));
        return Regex.Matches(item, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
            .Select(m => new KeyValuePair<string, string>(
                WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .ToList();
    }

    private async Task<HttpStatusCode> RefreshStatusAsync(Setup s)
    {
        var (status, body) = await _smart.RefreshAsync(s.App, s.Refresh);
        if (status != HttpStatusCode.OK)
            body.GetProperty("error").GetString().Should().Be("invalid_grant");
        return status;
    }

    // ── The person: /account/apps ──────────────────────────────────────────

    [Fact]
    public async Task AccountApps_ListsTheApproval_AndWithdrawingItEndsRefresh_AndAsksAgain()
    {
        var s = await ApprovedAppAsync();

        var page = await s.Member.GetAsync("/account/apps");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain(s.App).And.Contain("patient/*.read").And.Contain("offline_access");

        var revoke = await s.Member.PostAsync("/account/apps/revoke", new FormUrlEncodedContent(RevokeForm(html, s.App)));
        revoke.StatusCode.Should().Be(HttpStatusCode.Redirect);
        revoke.Headers.Location!.ToString().Should().Be("/account/apps?revoked=1");

        (await RefreshStatusAsync(s)).Should().Be(HttpStatusCode.BadRequest, "the approval is withdrawn");
        (await (await s.Member.GetAsync("/account/apps")).Content.ReadAsStringAsync()).Should().NotContain(s.App);

        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("v"u8.ToArray()))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var again = await s.Member.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = s.App, ["redirect_uri"] = SmartAuthDriver.RedirectUri,
            ["scope"] = Scope, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = "s",
        }));
        again.StatusCode.Should().Be(HttpStatusCode.OK, "the consent page again");
        (await again.Content.ReadAsStringAsync()).Should().Contain("consent_token");
    }

    [Fact]
    public async Task AccountApps_WithoutASession_GoesToLogin()
    {
        var resp = await _smart.Anonymous().GetAsync("/account/apps");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().StartWith("/account/login");

        (await _smart.Anonymous().PostAsync("/account/apps/revoke", new FormUrlEncodedContent([])))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_WithoutAValidProof_OrWithAnotherPersonsForm_DoesNothing()
    {
        var victim = await ApprovedAppAsync();
        var form = RevokeForm(await (await victim.Member.GetAsync("/account/apps")).Content.ReadAsStringAsync(), victim.App);
        var approval = form.Single(p => p.Key == "approval").Value;

        // A cross-site post has no proof; a forged one does not verify.
        foreach (var attempt in new[]
                 {
                     new List<KeyValuePair<string, string>> { new("approval", approval) },
                     new List<KeyValuePair<string, string>> { new("approval", approval), new("revoke_token", "x") },
                 })
        {
            (await victim.Member.PostAsync("/account/apps/revoke", new FormUrlEncodedContent(attempt)))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        // Another person posting the victim's form: the proof is bound to the victim.
        var other = await _smart.LinkedMemberAsync(victim.Tenant, "mbr-other-" + Guid.NewGuid().ToString("N")[..4]);
        (await other.PostAsync("/account/apps/revoke", new FormUrlEncodedContent(form)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await RefreshStatusAsync(victim)).Should().Be(HttpStatusCode.OK, "nothing was revoked");
    }

    // ── The administrator: admin API ──────────────────────────────────────

    [Fact]
    public async Task Admin_ListsAndRevokesOneApproval_OfItsOwnTenantsClient()
    {
        var s = await ApprovedAppAsync();
        var admin = _smart.Admin(s.Tenant);

        var list = await admin.GetAsync($"/api/admin/smart/clients/{s.App}/approvals");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var approvals = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        approvals.GetArrayLength().Should().Be(1);
        var id = approvals[0].GetProperty("id").GetString()!;

        // Another tenant's administrator sees and revokes nothing.
        var outsider = _smart.Admin(SmartAuthDriver.NewTenant());
        (await outsider.GetAsync($"/api/admin/smart/clients/{s.App}/approvals")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.DeleteAsync($"/api/admin/smart/clients/{s.App}/approvals/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await outsider.DeleteAsync($"/api/admin/smart/clients/{s.App}/approvals")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await RefreshStatusAsync(s)).Should().Be(HttpStatusCode.OK);

        // Without settings:manage: forbidden.
        (await _smart.Admin(s.Tenant, CloudHealthOffice.Infrastructure.Security.ChoRolePermissions.MemberServices)
                .DeleteAsync($"/api/admin/smart/clients/{s.App}/approvals/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await admin.DeleteAsync($"/api/admin/smart/clients/{s.App}/approvals/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await RefreshStatusAsync(s)).Should().Be(HttpStatusCode.BadRequest);
        (await admin.DeleteAsync($"/api/admin/smart/clients/{s.App}/approvals/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_RevokesEveryApprovalOfAClient_OrOnlyOnePersons()
    {
        var first = await ApprovedAppAsync();
        var second = await ApprovedAppAsync(first.Tenant);   // another app of the same tenant
        // A second member on the first app.
        var otherMember = await _smart.LinkedMemberAsync(first.Tenant, "mbr-b-" + Guid.NewGuid().ToString("N")[..4]);
        var outcome = await SmartAuthDriver.AuthorizeAsync(otherMember, first.App, Scope);
        var (_, body) = await _smart.ExchangeCodeAsync(first.App, outcome);
        var otherOnFirst = first with { Member = otherMember, Refresh = body.GetProperty("refresh_token").GetString()! };

        var admin = _smart.Admin(first.Tenant);
        var approvals = JsonDocument.Parse(await admin.GetStringAsync($"/api/admin/smart/clients/{first.App}/approvals")).RootElement;
        approvals.GetArrayLength().Should().Be(2);
        var subjects = approvals.EnumerateArray().Select(a => a.GetProperty("subject").GetString()!).ToList();

        // One person's only (the subject is the first one listed; either will do).
        var one = await admin.DeleteAsync($"/api/admin/smart/clients/{first.App}/approvals?subject={Uri.EscapeDataString(subjects[0])}");
        (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32().Should().Be(1);
        var statuses = new[] { await RefreshStatusAsync(first), await RefreshStatusAsync(otherOnFirst) };
        statuses.Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest });

        // Everyone's on the first app; the second app is untouched.
        var all = await admin.DeleteAsync($"/api/admin/smart/clients/{first.App}/approvals");
        (await all.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revoked").GetInt32().Should().Be(1);
        (await RefreshStatusAsync(first)).Should().Be(HttpStatusCode.BadRequest);
        (await RefreshStatusAsync(otherOnFirst)).Should().Be(HttpStatusCode.BadRequest);
        (await RefreshStatusAsync(second)).Should().Be(HttpStatusCode.OK);
    }

    // ── Refresh tokens from before consent ────────────────────────────────

    [Fact]
    public async Task RefreshToken_NotRestingOnAnApproval_IsRefused()
    {
        var s = await ApprovedAppAsync();

        // What a refresh token issued before consent existed rests on: an
        // ad-hoc authorization OpenIddict created by itself.
        using (var scope = _fixture.Factory.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            var appId = await applications.GetIdAsync((await applications.FindByClientIdAsync(s.App))!);
            await foreach (var authorization in authorizations.FindByApplicationIdAsync(appId!))
            {
                var descriptor = new OpenIddictAuthorizationDescriptor();
                await authorizations.PopulateAsync(descriptor, authorization);
                descriptor.Type = AuthorizationTypes.AdHoc;
                await authorizations.UpdateAsync(authorization, descriptor);
            }
        }

        (await RefreshStatusAsync(s)).Should().Be(HttpStatusCode.BadRequest, "the app must ask the person again");
    }
}
