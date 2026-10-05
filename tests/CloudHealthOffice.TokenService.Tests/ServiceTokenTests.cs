using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.ServiceTokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>
/// Test-only Entra app-only tokens, as a managed identity (workload identity)
/// receives them for token-service's service-token API. Signed with the test
/// Entra key; token-service is given its JWKS instead of Entra's metadata.
/// </summary>
public static class TestWorkloadIdentity
{
    public const string ServiceApiAppId = "5e1f0000-0000-0000-0000-00000000a5c1";
    public const string ServiceApiAppIdUri = "api://" + ServiceApiAppId;
    public const string AppRole = "Cho.ServiceToken";

    public const string ClaimsOid = "c1a10000-0000-0000-0000-000000000001";
    public const string ClaimsAppId = "c1a10000-0000-0000-0000-0000000000a1";
    public const string CapitationOid = "ca910000-0000-0000-0000-000000000002";
    public const string UnknownOid = "0bad0000-0000-0000-0000-000000000003";

    public static string Jwks()
    {
        var p = TestEntra.Key.Rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = TestEntra.Key.KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) },
            },
        });
    }

    /// <summary>An app-only access token for a managed identity, shaped like Entra v1 (sts.windows.net, appid).</summary>
    public static string Token(
        string oid,
        string? appId = null,
        string tid = Ids.PlatformDirectory,
        string aud = ServiceApiAppIdUri,
        string? issuer = null,
        DateTime? expires = null,
        string? scp = null,
        string[]? roles = null,
        string? sub = null,
        string? idtyp = "app",
        SecurityKey? key = null)
    {
        var exp = expires ?? DateTime.UtcNow.AddHours(1);
        var claims = new Dictionary<string, object>
        {
            ["tid"] = tid,
            ["oid"] = oid,
            ["sub"] = sub ?? oid,
            ["appid"] = appId ?? oid.Replace("0000-0000-0000-0000", "1111-1111-1111-1111", StringComparison.Ordinal),
            ["roles"] = roles ?? new[] { AppRole },
            ["ver"] = "1.0",
        };
        if (scp != null) claims["scp"] = scp;
        if (idtyp != null) claims["idtyp"] = idtyp;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? $"https://sts.windows.net/{tid}/",
            Audience = aud,
            IssuedAt = exp.AddHours(-1),
            NotBefore = exp.AddHours(-1),
            Expires = exp,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? TestEntra.Key, SecurityAlgorithms.RsaSha256),
        });
    }

    public static Dictionary<string, string?> Settings() => new()
    {
        ["ServiceTokens:Enabled"] = "true",
        ["ServiceTokens:EntraTenantId"] = Ids.PlatformDirectory,
        ["ServiceTokens:Audiences:0"] = ServiceApiAppIdUri,
        ["ServiceTokens:Audiences:1"] = ServiceApiAppId,
        ["ServiceTokens:RequiredAppRole"] = AppRole,
        ["ServiceTokens:JwksJson"] = Jwks(),
        ["ServiceTokens:Issuer"] = "cho-token-service-svc",
        ["ServiceTokens:TokenLifetime"] = "00:10:00",
        ["ServiceTokens:ServiceClients:0:ClientId"] = "claims-service",
        ["ServiceTokens:ServiceClients:0:ObjectId"] = ClaimsOid,
        ["ServiceTokens:ServiceClients:0:AppId"] = ClaimsAppId,
        ["ServiceTokens:ServiceClients:1:ClientId"] = "capitation-service",
        ["ServiceTokens:ServiceClients:1:ObjectId"] = CapitationOid,
    };
}

/// <summary>
/// <c>POST /v1/token/service</c>: a CHO service's workload-identity token for
/// a CHO service token naming that service only, and how callees treat it.
/// </summary>
public sealed class ServiceTokenTests : IDisposable
{
    private readonly TokenServiceFactory _factory = new() { ExtraSettings = TestWorkloadIdentity.Settings() };

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> Exchange(string? entraToken, object? body)
    {
        var client = _factory.CreateClient();
        if (entraToken != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", entraToken);
        return body == null
            ? await client.PostAsync(ServiceTokenEndpoint.Path, null)
            : await client.PostAsJsonAsync(ServiceTokenEndpoint.Path, body);
    }

    private async Task<string> IssueAsync(string oid, string tenant, string? appId = null)
    {
        var response = await Exchange(TestWorkloadIdentity.Token(oid, appId), new { tenantId = tenant });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.JsonAsync()).GetProperty("access_token").GetString()!;
    }

    private IEnumerable<string> AuditLines(string outcome)
        => _factory.Logs.Entries
            .Where(e => e.Category == "CloudHealthOffice.TokenService.Audit" && e.Message.Contains("CHO service token " + outcome))
            .Select(e => e.Message);

    // ── Issuance ────────────────────────────────────────────────────────

    [Fact]
    public async Task Registered_identity_gets_a_service_token_naming_its_own_client_id()
    {
        var response = await Exchange(
            TestWorkloadIdentity.Token(TestWorkloadIdentity.ClaimsOid, TestWorkloadIdentity.ClaimsAppId), new { tenantId = "acme" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.JsonAsync();
        body.GetProperty("expires_in").GetInt32().Should().Be(600);
        body.GetProperty("tenant_id").GetString().Should().Be("acme");
        body.GetProperty("client_id").GetString().Should().Be("claims-service");

        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Issuer.Should().Be("cho-token-service-svc");
        jwt.Audiences.Should().Equal("cho-api");
        jwt.Subject.Should().Be("claims-service");
        jwt.GetClaim("azp").Value.Should().Be("claims-service");
        jwt.GetClaim("tenant_id").Value.Should().Be("acme");
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().Equal(ChoServiceRole.Name);
        jwt.Claims.Should().NotContain(c => c.Type == "permissions");
        (jwt.ValidTo - jwt.IssuedAt).Should().Be(TimeSpan.FromMinutes(10));

        // Exactly the claims a locally minted service token carries, so callees do not change.
        var local = new JsonWebToken(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("claims-service", "acme"));
        jwt.Claims.Select(c => c.Type).Distinct().Should().BeEquivalentTo(local.Claims.Select(c => c.Type).Distinct());

        AuditLines("issued").Should().ContainSingle(l =>
            l.Contains("client=claims-service") && l.Contains("oid=" + TestWorkloadIdentity.ClaimsOid)
            && l.Contains("appid=" + TestWorkloadIdentity.ClaimsAppId) && l.Contains("tenant=acme"));
    }

    [Fact]
    public async Task V2_token_with_client_id_audience_is_accepted()
    {
        var token = TestWorkloadIdentity.Token(TestWorkloadIdentity.CapitationOid,
            aud: TestWorkloadIdentity.ServiceApiAppId, issuer: $"https://login.microsoftonline.com/{Ids.PlatformDirectory}/v2.0");
        var response = await Exchange(token, new { tenantId = "beta" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.JsonAsync()).GetProperty("client_id").GetString().Should().Be("capitation-service");
    }

    // ── Refusals ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_identity_is_forbidden_and_audited()
    {
        var response = await Exchange(TestWorkloadIdentity.Token(TestWorkloadIdentity.UnknownOid), new { tenantId = "acme" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("unknown_service");
        AuditLines("refused").Should().ContainSingle(l =>
            l.Contains("reason=unknown_service") && l.Contains("oid=" + TestWorkloadIdentity.UnknownOid) && l.Contains("tenant=acme"));
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task Registered_object_id_with_another_app_id_is_forbidden()
    {
        // The registry pins claims-service's client id as well as its object id.
        var response = await Exchange(
            TestWorkloadIdentity.Token(TestWorkloadIdentity.ClaimsOid, appId: "eeeeeeee-0000-0000-0000-000000000000"), new { tenantId = "acme" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    public static TheoryData<string, string> InvalidEntraTokens()
    {
        const string oid = TestWorkloadIdentity.CapitationOid;
        var other = new RsaSecurityKey(RSA.Create(2048)) { KeyId = TestEntra.Key.KeyId };
        return new TheoryData<string, string>
        {
            { TestWorkloadIdentity.Token(oid, aud: Ids.ApiAppIdUri), "wrong_audience" },
            { TestWorkloadIdentity.Token(oid, aud: "cho-api"), "wrong_audience" },
            { TestWorkloadIdentity.Token(oid, issuer: "https://attacker.example/"), "wrong_issuer" },
            // Another directory's token, self-consistent (iss names its own tid): not CHO's directory.
            { TestWorkloadIdentity.Token(oid, tid: Ids.AcmeDirectory), "wrong_issuer" },
            // CHO's issuer but another tid claim.
            { TestWorkloadIdentity.Token(oid, tid: Ids.AcmeDirectory, issuer: $"https://sts.windows.net/{Ids.PlatformDirectory}/"), "wrong_directory" },
            { TestWorkloadIdentity.Token(oid, expires: DateTime.UtcNow.AddMinutes(-10)), "expired" },
            { TestWorkloadIdentity.Token(oid, key: other), "bad_signature" },
            { TestWorkloadIdentity.Token(oid, scp: "Cho.Token"), "delegated_token" },
            { TestWorkloadIdentity.Token(oid, idtyp: "user"), "not_app_token" },
            { TestWorkloadIdentity.Token(oid, sub: "11111111-2222-3333-4444-555555555555"), "not_app_token" },
            { TestWorkloadIdentity.Token(oid, roles: Array.Empty<string>()), "missing_app_role" },
            { "not-a-jwt", "invalid_token" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidEntraTokens))]
    public async Task Invalid_workload_identity_token_is_unauthorized(string entraToken, string reason)
    {
        var response = await Exchange(entraToken, new { tenantId = "acme" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid_token");
        AuditLines("refused").Should().ContainSingle(l => l.Contains("reason=" + reason) && l.Contains("tenant=acme"));
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task A_users_delegated_cho_token_request_is_not_a_workload_identity()
    {
        // The portal's user token for Cho.Token: delegated, other audience.
        var response = await Exchange(TestEntra.Token(Ids.PlatformDirectory, TestWorkloadIdentity.CapitationOid), new { tenantId = "acme" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_token_is_unauthorized()
    {
        (await Exchange(null, new { tenantId = "acme" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AuditLines("refused").Should().ContainSingle(l => l.Contains("reason=missing_token"));
    }

    [Fact]
    public async Task Missing_or_malformed_tenant_is_a_bad_request()
    {
        var token = TestWorkloadIdentity.Token(TestWorkloadIdentity.CapitationOid);
        (await Exchange(token, new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Exchange(token, null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Exchange(token, new { tenantId = "bad\"tenant" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task Endpoint_is_absent_when_not_enabled()
    {
        using var plain = new TokenServiceFactory();
        var client = plain.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            TestWorkloadIdentity.Token(TestWorkloadIdentity.CapitationOid));
        (await client.PostAsJsonAsync(ServiceTokenEndpoint.Path, new { tenantId = "acme" })).StatusCode
            .Should().NotBe(HttpStatusCode.OK);
    }

    // ── token-service's own calls ───────────────────────────────────────

    [Fact]
    public async Task Without_a_local_key_token_service_signs_its_own_tenant_service_token_under_the_service_issuer()
    {
        using var deployed = new TokenServiceFactory { ExtraSettings = TestWorkloadIdentity.Settings(), LocalServiceTokenKey = false };
        var client = deployed.ClientWith(TestEntra.Token(Ids.AcmeDirectory, "aaaaaaaa-0000-0000-0000-000000000001"));
        await client.ExchangeAsync(null);

        var header = deployed.TenantService.AuthorizationHeaders.Should().NotBeEmpty().And.Subject.First();
        header.Should().StartWith("Bearer ");
        var jwt = new JsonWebToken(header![7..]);
        jwt.Issuer.Should().Be("cho-token-service-svc");
        jwt.Subject.Should().Be("token-service");
        jwt.GetClaim("azp").Value.Should().Be("token-service");
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().Equal(ChoServiceRole.Name);
    }

    // ── Under the shared ChoAuth layer ──────────────────────────────────

    [Fact]
    public async Task Issued_token_passes_its_own_RequireServiceClient_and_no_other()
    {
        await using var service = await ChoServiceAsync();
        var client = Client(service, await IssueAsync(TestWorkloadIdentity.ClaimsOid, "acme", TestWorkloadIdentity.ClaimsAppId));

        (await client.GetAsync("/svc/claims-service")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/svc/capitation-service")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var whoami = await (await client.GetAsync("/whoami")).JsonAsync();
        whoami.GetProperty("isService").GetBoolean().Should().BeTrue();
        whoami.GetProperty("tenant").GetString().Should().Be("acme");
    }

    [Fact]
    public async Task Token_signed_with_the_old_shared_key_is_rejected_by_a_callee_trusting_only_token_service()
    {
        await using var service = await ChoServiceAsync();

        // What a compromised service could do before: mint capitation-service's
        // identity with the shared cho-internal key.
        var oldShared = ChoTokenIssuer.FromKeys("cho-internal", "cho-api", SharedInternalKeyPem, null, TimeSpan.FromMinutes(5))
            .IssueServiceToken("capitation-service", "acme");
        (await Client(service, oldShared).GetAsync("/svc/capitation-service")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The same key, now claiming token-service's service issuer: wrong signature.
        var forged = ChoTokenIssuer.FromKeys("cho-token-service-svc", "cho-api", SharedInternalKeyPem, null, TimeSpan.FromMinutes(5))
            .IssueServiceToken("capitation-service", "acme");
        (await Client(service, forged).GetAsync("/svc/capitation-service")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task User_issuer_token_claiming_the_service_role_is_not_a_service()
    {
        await using var service = await ChoServiceAsync();
        // token-service's key, but its user issuer: never a service actor.
        var asUser = ChoTokenIssuer.FromKeys("cho-token-service", "cho-api", _factory.SigningKeyPem, null, TimeSpan.FromMinutes(5))
            .IssueServiceToken("capitation-service", "acme");
        (await Client(service, asUser).GetAsync("/svc/capitation-service")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Configuration ───────────────────────────────────────────────────

    public static TheoryData<string, string?, string> BadRegistrations() => new()
    {
        { "*", TestWorkloadIdentity.ClaimsOid, "wildcard client id" },
        { "claims-service", "*", "wildcard object id" },
        { "claims-service", null, "missing object id" },
        { "wf-enrollment-import", TestWorkloadIdentity.ClaimsOid, "workload client id" },
        { "Claims Service", TestWorkloadIdentity.ClaimsOid, "not a service name" },
    };

    [Theory]
    [MemberData(nameof(BadRegistrations))]
    public void Registration_that_is_not_exactly_one_identity_for_one_service_fails_startup(string clientId, string? objectId, string _)
    {
        var options = ValidOptions();
        options.ServiceClients = [new ServiceClientRegistration { ClientId = clientId, ObjectId = objectId }];
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", isDevelopment: false)))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Same_identity_or_client_registered_twice_fails_startup()
    {
        var options = ValidOptions();
        options.ServiceClients.Add(new ServiceClientRegistration { ClientId = "member-service", ObjectId = TestWorkloadIdentity.ClaimsOid });
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false))).Should().Throw<InvalidOperationException>();

        options = ValidOptions();
        options.ServiceClients.Add(new ServiceClientRegistration { ClientId = "claims-service", ObjectId = TestWorkloadIdentity.UnknownOid });
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false))).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("cho-token-service", "00:05:00")]
    [InlineData("cho-workload", "00:05:00")]
    [InlineData("cho-token-service-svc", "00:30:00")]
    public void Issuer_shared_with_another_kind_or_a_long_lifetime_fails_startup(string issuer, string lifetime)
    {
        var options = ValidOptions();
        options.Issuer = issuer;
        options.TokenLifetime = TimeSpan.Parse(lifetime);
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Valid_options_pass()
        => ValidOptions().Validate("cho-token-service", "cho-workload", isDevelopment: false);

    [Fact]
    public void Shipped_service_registry_validates_once_object_ids_are_filled_in()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "src/services/token-service/k8s/token-service-deployment.yaml"));
        const string marker = "service-clients.json: |";
        var start = manifest.IndexOf(marker, StringComparison.Ordinal);
        var json = manifest[(start + marker.Length)..manifest.IndexOf("\n---", start, StringComparison.Ordinal)];
        var file = Path.GetTempFileName();
        try
        {
            var n = 0;
            File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(
                json, "<[A-Z0-9_]+_MANAGED_IDENTITY_OBJECT_ID>", _ => $"00000000-0000-0000-0000-{++n:D12}"));
            var options = ValidOptions();
            options.ServiceClients.Clear();
            options.ServiceClientsFile = file;
            options.Validate("cho-token-service", "cho-workload", isDevelopment: false);

            var clients = options.AllServiceClients().Select(c => c.ClientId).ToList();
            clients.Should().Contain(["claims-service", "capitation-service", "premium-billing-service", "payment-service", "tenant-service"]);
            clients.Should().NotContain("token-service", "token-service signs its own token");

            // As shipped, the placeholders are not object ids: startup fails until they are set.
            File.WriteAllText(file, json);
            ((Action)(() => options.Validate("cho-token-service", "cho-workload", false))).Should().Throw<InvalidOperationException>();
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(Path.Combine(dir.FullName, "src", "services", "token-service")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static ServiceTokenOptions ValidOptions() => new()
    {
        Enabled = true,
        EntraTenantId = Ids.PlatformDirectory,
        Audiences = { TestWorkloadIdentity.ServiceApiAppIdUri },
        ServiceClients = { new ServiceClientRegistration { ClientId = "claims-service", ObjectId = TestWorkloadIdentity.ClaimsOid } },
    };

    // ── helpers ─────────────────────────────────────────────────────────

    private static readonly string SharedInternalKeyPem = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    private static string PublicPem(string privatePem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);
        return ec.ExportSubjectPublicKeyInfoPem();
    }

    private static HttpClient Client(WebApplication app, string token)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// A CHO backend service in Production mode, configured as after the
    /// migration: it trusts token-service's user issuer and its service issuer
    /// (same key, Kind=Service), and no longer the shared cho-internal key.
    /// </summary>
    private async Task<WebApplication> ChoServiceAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        var tokenServicePublic = PublicPem(_factory.SigningKeyPem);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ChoAuth:Audience"] = "cho-api",
            ["ChoAuth:Issuers:0:Issuer"] = "cho-token-service",
            ["ChoAuth:Issuers:0:PublicKeyPem"] = tokenServicePublic,
            ["ChoAuth:Issuers:0:Kind"] = "User",
            ["ChoAuth:Issuers:1:Issuer"] = "cho-token-service-svc",
            ["ChoAuth:Issuers:1:PublicKeyPem"] = tokenServicePublic,
            ["ChoAuth:Issuers:1:Kind"] = "Service",
        });
        builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UseChoAuthentication();

        app.MapGet("/svc/claims-service", () => Results.Ok())
            .RequireAuthorization(new RequireServiceClientAttribute("claims-service"));
        app.MapGet("/svc/capitation-service", () => Results.Ok())
            .RequireAuthorization(new RequireServiceClientAttribute("capitation-service"));
        app.MapGet("/whoami", (ICurrentActor actor) => Results.Json(new
        {
            sub = actor.UserId,
            tenant = actor.TenantId,
            isService = actor.IsService,
        })).RequireAuthorization();

        await app.StartAsync();
        return app;
    }
}
