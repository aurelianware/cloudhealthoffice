using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// Drives smart-auth-service the way real callers do: tenant administrators
/// through the admin API with CHO tokens, people through the development
/// login and the OAuth endpoints, backends through client_credentials.
/// </summary>
public sealed class SmartAuthDriver
{
    public const string RedirectUri = "http://localhost/cb";

    private readonly WebApplicationFactory<SmartAuthService.Program> _factory;

    public SmartAuthDriver(WebApplicationFactory<SmartAuthService.Program> factory) => _factory = factory;

    public static string NewTenant() => "t-" + Guid.NewGuid().ToString("N")[..10];

    public HttpClient Anonymous() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>A client carrying a CHO user token for <paramref name="tenant"/>.</summary>
    public HttpClient Admin(string tenant, params string[] roles) => AdminAs(tenant, "admin-" + tenant, roles);

    public HttpClient AdminAs(string tenant, string subject, params string[] roles)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(subject, tenant,
                roles.Length > 0 ? roles : [ChoRolePermissions.TenantAdmin]));
        return client;
    }

    public async Task<(string ClientId, string? Secret)> RegisterClientAsync(
        string tenant, string kind, params string[] scopes)
    {
        var resp = await Admin(tenant).PostAsJsonAsync("/api/admin/smart/clients", new
        {
            kind,
            redirectUris = kind == "backend" ? null : new[] { RedirectUri },
            scopes,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (body.GetProperty("clientId").GetString()!,
                body.TryGetProperty("clientSecret", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
    }

    public async Task<string> IssueMemberCodeAsync(string tenant, string memberId)
    {
        var resp = await Admin(tenant, ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync("/api/admin/smart/member-enrolments", new { memberId });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;
    }

    public async Task<string> IssueProviderCodeAsync(string tenant, string providerId, string npi)
    {
        var resp = await Admin(tenant, ChoRolePermissions.ProviderRelations)
            .PostAsJsonAsync("/api/admin/smart/provider-enrolments", new { providerId, npi });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>A browser session signed in with the development login.</summary>
    public async Task<HttpClient> SignInAsync(string username)
    {
        var browser = Anonymous();
        var resp = await browser.PostAsync("/account/login?returnUrl=%2F", new FormUrlEncodedContent(
        [
            new("username", username),
            new("password", "Password123!"),
        ]));
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().NotContain("error");
        return browser;
    }

    public static async Task<HttpResponseMessage> RedeemAsync(HttpClient browser, string code)
        => await browser.PostAsync("/account/link", new FormUrlEncodedContent([new("code", code)]));

    /// <summary>A signed-in person bound to (tenant, member) through the enrolment flow.</summary>
    public async Task<HttpClient> LinkedMemberAsync(string tenant, string memberId, string? username = null)
    {
        var code = await IssueMemberCodeAsync(tenant, memberId);
        var browser = await SignInAsync(username ?? "member-" + Guid.NewGuid().ToString("N")[..8]);
        var resp = await RedeemAsync(browser, code);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return browser;
    }

    public async Task<HttpClient> LinkedProviderAsync(string tenant, string providerId, string npi)
    {
        var code = await IssueProviderCodeAsync(tenant, providerId, npi);
        var browser = await SignInAsync("provider-" + Guid.NewGuid().ToString("N")[..8]);
        var resp = await RedeemAsync(browser, code);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return browser;
    }

    public sealed record AuthorizeOutcome(string? Code, string? Error, string Verifier, string RedirectUri);

    /// <summary>
    /// The authorization request; when the consent page comes back, the person
    /// answers it with <paramref name="consent"/> ("approve" by default; null
    /// leaves the page unanswered and fails the call).
    /// </summary>
    public static async Task<AuthorizeOutcome> AuthorizeAsync(
        HttpClient browser, string clientId, string scope, string? launch = null,
        IDictionary<string, string>? extra = null, Action<HttpRequestMessage>? tamper = null,
        string redirectUri = RedirectUri, string? consent = "approve")
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "s1",
        };
        if (launch != null) query["launch"] = launch;
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) query[k] = v;

        var request = new HttpRequestMessage(HttpMethod.Get, QueryHelpers.AddQueryString("/connect/authorize", query));
        tamper?.Invoke(request);
        var resp = await browser.SendAsync(request);
        if (resp.StatusCode == HttpStatusCode.OK && consent != null)
        {
            var page = await resp.Content.ReadAsStringAsync();
            var form = ConsentForm(page);
            form.Add(new("consent_decision", consent));
            resp = await browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(form));
        }
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect, await resp.Content.ReadAsStringAsync());

        var location = resp.Headers.Location!;
        location.ToString().Should().StartWith(redirectUri, "the outcome goes back to the client either way");
        var parsed = QueryHelpers.ParseQuery(location.Query);
        return new AuthorizeOutcome(
            parsed.TryGetValue("code", out var c) ? c.ToString() : null,
            parsed.TryGetValue("error", out var e) ? e.ToString() : null,
            verifier,
            redirectUri);
    }

    /// <summary>The hidden fields of a consent page, as a browser would post them.</summary>
    public static List<KeyValuePair<string, string>> ConsentForm(string page)
    {
        page.Should().Contain("consent_token", "the consent page is expected here");
        return System.Text.RegularExpressions.Regex
            .Matches(page, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
            .Select(m => new KeyValuePair<string, string>(
                System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)))
            .ToList();
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> ExchangeCodeAsync(
        string clientId, AuthorizeOutcome outcome, string? secret = null,
        IDictionary<string, string>? extra = null, Action<HttpRequestMessage>? tamper = null)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = outcome.Code!,
            ["redirect_uri"] = outcome.RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = outcome.Verifier,
        };
        if (secret != null) form["client_secret"] = secret;
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) form[k] = v;
        return await TokenAsync(form, tamper);
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> RefreshAsync(string clientId, string refreshToken)
        => await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        });

    public async Task<(HttpStatusCode Status, JsonElement Body)> ClientCredentialsAsync(
        string clientId, string secret, string scope, Action<HttpRequestMessage>? tamper = null)
        => await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope,
        }, tamper);

    public async Task<(HttpStatusCode Status, JsonElement Body)> TokenAsync(
        Dictionary<string, string> form, Action<HttpRequestMessage>? tamper = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/connect/token") { Content = new FormUrlEncodedContent(form) };
        tamper?.Invoke(request);
        var resp = await Anonymous().SendAsync(request);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        return (resp.StatusCode, body);
    }

    /// <summary>Full member flow: authorize + exchange, returning the access token.</summary>
    public async Task<string> MemberAccessTokenAsync(
        HttpClient browser, string clientId, string scope = "openid launch/patient patient/*.read",
        string redirectUri = RedirectUri)
    {
        var outcome = await AuthorizeAsync(browser, clientId, scope, redirectUri: redirectUri);
        outcome.Error.Should().BeNull();
        var (status, body) = await ExchangeCodeAsync(clientId, outcome);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return body.GetProperty("access_token").GetString()!;
    }

    public static JwtSecurityToken Read(string jwt) => new JwtSecurityTokenHandler().ReadJwtToken(jwt);

    public static string? Claim(JwtSecurityToken token, string type)
        => token.Claims.FirstOrDefault(c => c.Type == type)?.Value;

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
