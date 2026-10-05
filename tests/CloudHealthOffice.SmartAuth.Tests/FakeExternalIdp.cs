using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server.AspNetCore;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>
/// An in-process OpenID Connect provider shaped like Entra External ID:
/// discovery, JWKS and a token endpoint served from an HttpMessageHandler
/// that is installed as the OIDC handler's backchannel. Its own RSA key signs
/// the ID tokens. The token endpoint checks the PKCE verifier, the client
/// credential and the redirect URI exactly as a real IdP would.
/// </summary>
public sealed class FakeExternalIdp : HttpMessageHandler
{
    public const string Authority = "https://fake-ciam.test/11111111-2222-3333-4444-555555555555/v2.0";
    public const string Issuer = "https://11111111-2222-3333-4444-555555555555.fake-ciam.test/11111111-2222-3333-4444-555555555555/v2.0";
    public const string ClientId = "6f1c2c5e-0000-4000-8000-00000000c1d0";
    public const string ClientSecret = "fake-idp-client-secret";
    public const string AuthorizeEndpoint = "https://fake-ciam.test/oauth2/v2.0/authorize";

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "fake-idp-key-1" };
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();

    /// <summary>The issuer the discovery document advertises (normally <see cref="Issuer"/>).</summary>
    public string DiscoveryIssuer { get; set; } = Issuer;

    public ConcurrentQueue<Dictionary<string, string>> TokenRequests { get; } = new();

    public sealed class IdToken
    {
        public string Issuer { get; set; } = FakeExternalIdp.Issuer;
        public string Audience { get; set; } = ClientId;
        public string? Nonce { get; set; }
        public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
        public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(1);
        public Dictionary<string, object> Claims { get; } = new();
        public SecurityKey? SignWith { get; set; }
    }

    private sealed record PendingCode(IdToken Token, string CodeChallenge, string RedirectUri);

    /// <summary>The outcome of the challenge redirect: the parameters sent to the IdP.</summary>
    public sealed record Challenge(HttpResponseMessage Response, IReadOnlyDictionary<string, string> Query)
    {
        public string State => Query["state"];
        public string Nonce => Query["nonce"];
        public string RedirectUri => Query["redirect_uri"];
    }

    /// <summary>Settings and backchannel that point smart-auth-service at this IdP.</summary>
    public void Apply(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder, string displayName = "Example ID")
    {
        builder.UseSetting("SmartAuth:ExternalLogin:Enabled", "true");
        builder.UseSetting("SmartAuth:ExternalLogin:Authority", Authority);
        builder.UseSetting("SmartAuth:ExternalLogin:ClientId", ClientId);
        builder.UseSetting("SmartAuth:ExternalLogin:ClientSecret", ClientSecret);
        builder.UseSetting("SmartAuth:ExternalLogin:ExpectedIssuer", Issuer);
        builder.UseSetting("SmartAuth:ExternalLogin:DisplayName", displayName);
        builder.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>("external-idp", o => o.BackchannelHttpHandler = this));
    }

    /// <summary>A smart-auth host (from <paramref name="factory"/>) that trusts this IdP.</summary>
    public WebApplicationFactory<SmartAuthService.Program> Host(
        WebApplicationFactory<SmartAuthService.Program> factory, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? more = null)
        => factory.WithWebHostBuilder(b =>
        {
            Apply(b);
            more?.Invoke(b);
        });

    /// <summary>GET /account/external-login → the redirect to the IdP.</summary>
    public static async Task<Challenge> StartAsync(HttpClient browser, string returnUrl = "/account/link")
    {
        var resp = await browser.GetAsync("/account/external-login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect, await resp.Content.ReadAsStringAsync());
        var location = resp.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be(AuthorizeEndpoint);
        var query = QueryHelpers.ParseQuery(location.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        return new Challenge(resp, query);
    }

    /// <summary>
    /// The IdP authenticates the user and redirects back with a code; the
    /// browser follows to the callback. <paramref name="token"/> shapes the
    /// ID token the code will be redeemed for (nonce defaults to the challenge's).
    /// </summary>
    public async Task<HttpResponseMessage> CompleteAsync(HttpClient browser, Challenge challenge, Action<IdToken>? token = null)
    {
        var idToken = new IdToken { Nonce = challenge.Nonce };
        idToken.Claims["sub"] = "pairwise-sub-" + Guid.NewGuid().ToString("N")[..8];
        idToken.Claims["oid"] = Guid.NewGuid().ToString();
        token?.Invoke(idToken);

        var code = "code-" + Guid.NewGuid().ToString("N");
        _codes[code] = new PendingCode(idToken, challenge.Query["code_challenge"], challenge.RedirectUri);

        var callback = new Uri(challenge.RedirectUri);
        return await browser.GetAsync(QueryHelpers.AddQueryString(callback.AbsolutePath,
            new Dictionary<string, string?> { ["code"] = code, ["state"] = challenge.State }));
    }

    /// <summary>Start + complete; returns the callback response.</summary>
    public async Task<HttpResponseMessage> SignInAsync(HttpClient browser, Action<IdToken>? token = null, string returnUrl = "/account/link")
        => await CompleteAsync(browser, await StartAsync(browser, returnUrl), token);

    /// <summary>The session ticket a Set-Cookie on <paramref name="response"/> carries, decrypted; null if none.</summary>
    public static Microsoft.AspNetCore.Authentication.AuthenticationTicket? SessionOf(
        WebApplicationFactory<SmartAuthService.Program> host, HttpResponseMessage response)
    {
        var cookie = SessionCookie(response);
        if (cookie is null) return null;
        var options = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Unprotect(Uri.UnescapeDataString(cookie));
    }

    /// <summary>The (non-empty) session cookie value set by <paramref name="response"/>.</summary>
    public static string? SessionCookie(HttpResponseMessage response)
        => SetCookies(response)
            .Where(c => c.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal))
            .Select(c => c[".AspNetCore.Cookies=".Length..].Split(';')[0])
            .FirstOrDefault(v => v.Length > 0);

    public static IEnumerable<string> SetCookies(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];

    // ── The IdP's endpoints ──────────────────────────────────────────────────

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == Authority + "/.well-known/openid-configuration")
        {
            return Json(new Dictionary<string, object>
            {
                ["issuer"] = DiscoveryIssuer,
                ["authorization_endpoint"] = AuthorizeEndpoint,
                ["token_endpoint"] = "https://fake-ciam.test/oauth2/v2.0/token",
                ["jwks_uri"] = "https://fake-ciam.test/discovery/v2.0/keys",
                ["end_session_endpoint"] = "https://fake-ciam.test/oauth2/v2.0/logout",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "pairwise" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            });
        }

        if (url == "https://fake-ciam.test/discovery/v2.0/keys")
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            return Json(new { keys = new[] { new { kty = jwk.Kty, use = "sig", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
        }

        if (url == "https://fake-ciam.test/oauth2/v2.0/token")
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            TokenRequests.Enqueue(form);

            if (form.GetValueOrDefault("grant_type") != "authorization_code"
                || !_codes.TryRemove(form.GetValueOrDefault("code") ?? "", out var pending)
                || form.GetValueOrDefault("client_id") != ClientId
                || form.GetValueOrDefault("client_secret") != ClientSecret
                || form.GetValueOrDefault("redirect_uri") != pending.RedirectUri
                || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? "")))
                    != pending.CodeChallenge)
            {
                return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
            }

            return Json(new
            {
                id_token = Sign(pending.Token),
                access_token = "opaque-access-token",
                token_type = "Bearer",
                expires_in = 3600,
            });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string Sign(IdToken token)
    {
        var claims = new Dictionary<string, object>(token.Claims);
        if (token.Nonce != null) claims["nonce"] = token.Nonce;
        var signingKey = token.SignWith ?? _key;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = token.Issuer,
            Audience = token.Audience,
            IssuedAt = token.IssuedAt,
            NotBefore = token.IssuedAt,
            Expires = token.IssuedAt + token.Lifetime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A key with the IdP's key id but different key material: a forged signature.</summary>
    public SecurityKey ForgedKey() => new RsaSecurityKey(RSA.Create(2048)) { KeyId = _key.KeyId };

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
}
