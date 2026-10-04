using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.Workload;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>A test Kubernetes cluster: its service-account issuer, key and projected tokens.</summary>
public static class TestCluster
{
    public const string Issuer = "https://oidc.test.example/cluster-1/";
    public const string Namespace = "cho-workflows";

    public static readonly RsaSecurityKey Key = new(RSA.Create(2048)) { KeyId = "cluster-sa-key" };

    /// <summary>The cluster's JWKS (public key only), as <c>kubectl get --raw /openid/v1/jwks</c> returns it.</summary>
    public static string Jwks()
    {
        var p = Key.Rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = Key.KeyId, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) },
            },
        });
    }

    /// <summary>A projected service-account token, shaped as the kubelet writes it.</summary>
    public static string Token(
        string serviceAccount,
        string ns = Namespace,
        string audience = "cho-token-service",
        string issuer = Issuer,
        DateTime? expires = null,
        TimeSpan? lifetime = null,
        bool podBound = true,
        bool kubernetesClaim = true,
        string? subject = null,
        SecurityKey? key = null)
    {
        var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
        var iat = exp - (lifetime ?? TimeSpan.FromMinutes(10));
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject ?? $"system:serviceaccount:{ns}:{serviceAccount}",
        };
        if (kubernetesClaim)
        {
            var k8s = new Dictionary<string, object>
            {
                ["namespace"] = ns,
                ["serviceaccount"] = new Dictionary<string, object> { ["name"] = serviceAccount, ["uid"] = Guid.NewGuid().ToString() },
            };
            if (podBound)
                k8s["pod"] = new Dictionary<string, object> { ["name"] = serviceAccount + "-pod-1", ["uid"] = Guid.NewGuid().ToString() };
            claims["kubernetes.io"] = k8s;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = iat,
            NotBefore = iat,
            Expires = exp,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <summary>
/// <c>POST /v1/token/workload</c>: a pod's projected service-account token
/// for a narrow CHO workload token, and how the shared ChoAuth layer treats it.
/// </summary>
public sealed class WorkloadTokenTests : IDisposable
{
    private const string Import = "wf-enrollment-import";
    private const string Onboarding = "wf-tenant-onboarding";

    private readonly TokenServiceFactory _factory = new()
    {
        ExtraSettings = new Dictionary<string, string?>
        {
            ["WorkloadTokens:Enabled"] = "true",
            ["WorkloadTokens:KubernetesIssuer"] = TestCluster.Issuer,
            ["WorkloadTokens:JwksJson"] = TestCluster.Jwks(),
            ["WorkloadTokens:Issuer"] = "cho-workload",
            ["WorkloadTokens:Registrations:0:Namespace"] = TestCluster.Namespace,
            ["WorkloadTokens:Registrations:0:ServiceAccount"] = Import,
            ["WorkloadTokens:Registrations:0:ClientId"] = Import,
            ["WorkloadTokens:Registrations:0:AllowedTenants:0"] = "acme",
            ["WorkloadTokens:Registrations:0:AllowedTenants:1"] = "beta",
            ["WorkloadTokens:Registrations:0:Permissions:0"] = "enrollment:process",
            ["WorkloadTokens:Registrations:0:Permissions:1"] = "members:write",
            ["WorkloadTokens:Registrations:1:Namespace"] = TestCluster.Namespace,
            ["WorkloadTokens:Registrations:1:ServiceAccount"] = Onboarding,
            ["WorkloadTokens:Registrations:1:ClientId"] = Onboarding,
            ["WorkloadTokens:Registrations:1:AllowedTenants:0"] = "*",
            ["WorkloadTokens:Registrations:1:Permissions:0"] = "settings:manage",
        },
    };

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> Exchange(string? k8sToken, object? body)
    {
        var client = _factory.CreateClient();
        if (k8sToken != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", k8sToken);
        return body == null
            ? await client.PostAsync(WorkloadTokenEndpoint.Path, null)
            : await client.PostAsJsonAsync(WorkloadTokenEndpoint.Path, body);
    }

    private async Task<string> IssueAsync(string serviceAccount, string tenant)
    {
        var response = await Exchange(TestCluster.Token(serviceAccount), new { tenantId = tenant });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.JsonAsync()).GetProperty("access_token").GetString()!;
    }

    private IEnumerable<string> AuditLines(string outcome)
        => _factory.Logs.Entries
            .Where(e => e.Category == "CloudHealthOffice.TokenService.Audit" && e.Message.Contains("CHO workload token " + outcome))
            .Select(e => e.Message);

    // ── Issuance ────────────────────────────────────────────────────────

    [Fact]
    public async Task Valid_service_account_token_gets_a_workload_token_with_exactly_the_allowed_permissions()
    {
        var response = await Exchange(TestCluster.Token(Import), new { tenantId = "acme" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.JsonAsync();
        body.GetProperty("expires_in").GetInt32().Should().Be(300);
        body.GetProperty("tenant_id").GetString().Should().Be("acme");
        body.GetProperty("client_id").GetString().Should().Be(Import);
        body.GetProperty("permissions").EnumerateArray().Select(p => p.GetString())
            .Should().Equal("enrollment:process", "members:write");

        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Issuer.Should().Be("cho-workload");
        jwt.Audiences.Should().Equal("cho-api");
        jwt.Subject.Should().Be(Import);
        jwt.GetClaim("azp").Value.Should().Be(Import);
        jwt.GetClaim("tenant_id").Value.Should().Be("acme");
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().Equal(ChoWorkloadRole.Name);
        jwt.Claims.Where(c => c.Type == "permissions").Select(c => c.Value)
            .Should().BeEquivalentTo("enrollment:process", "members:write");
        jwt.Claims.Should().NotContain(c => c.Value == ChoServiceRole.Name);
        (jwt.ValidTo - jwt.IssuedAt).Should().Be(TimeSpan.FromMinutes(5));

        AuditLines("issued").Should().ContainSingle(l =>
            l.Contains("client=" + Import) && l.Contains("sa=cho-workflows/" + Import) && l.Contains("pod=" + Import + "-pod-1")
            && l.Contains("tenant=acme") && l.Contains("permissions=enrollment:process,members:write"));
    }

    [Fact]
    public async Task Any_tenant_registration_gets_the_requested_tenant()
    {
        var token = await IssueAsync(Onboarding, "brand-new-tenant");
        new JsonWebToken(token).GetClaim("tenant_id").Value.Should().Be("brand-new-tenant");
    }

    // ── Refusals ────────────────────────────────────────────────────────

    public static TheoryData<string, string> InvalidKubernetesTokens()
    {
        var other = new RsaSecurityKey(RSA.Create(2048)) { KeyId = TestCluster.Key.KeyId };
        return new TheoryData<string, string>
        {
            { TestCluster.Token(Import, audience: "cho-api"), "wrong_audience" },
            { TestCluster.Token(Import, audience: "https://kubernetes.default.svc"), "wrong_audience" },
            { TestCluster.Token(Import, expires: DateTime.UtcNow.AddMinutes(-5)), "expired" },
            { TestCluster.Token(Import, issuer: "https://attacker.example/"), "wrong_issuer" },
            { TestCluster.Token(Import, key: other), "bad_signature" },
            { TestCluster.Token(Import, lifetime: TimeSpan.FromDays(365)), "token_lifetime_too_long" },
            { TestCluster.Token(Import, kubernetesClaim: false), "not_a_projected_token" },
            { TestCluster.Token(Import, podBound: false), "not_pod_bound" },
            { TestCluster.Token(Import, subject: $"system:serviceaccount:{TestCluster.Namespace}:{Onboarding}"), "subject_mismatch" },
            { TestCluster.Token(Import, subject: "admin"), "not_a_service_account" },
            { "not-a-jwt", "invalid_token" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidKubernetesTokens))]
    public async Task Invalid_service_account_token_is_refused(string k8sToken, string reason)
    {
        var response = await Exchange(k8sToken, new { tenantId = "acme" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("invalid_token");
        response.Content.Headers.ContentLength.Should().BeLessThan(100, "no token is returned");
        AuditLines("refused").Should().ContainSingle(l => l.Contains("reason=" + reason) && l.Contains("tenant=acme"));
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_token_is_refused()
    {
        var response = await Exchange(null, new { tenantId = "acme" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AuditLines("refused").Should().ContainSingle(l => l.Contains("reason=missing_token"));
    }

    [Fact]
    public async Task An_entra_token_is_not_a_kubernetes_token()
    {
        var response = await Exchange(TestEntra.Token(Ids.AcmeDirectory, "aaaaaaaa-0000-0000-0000-000000000001"), new { tenantId = "acme" });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_service_account_is_refused()
    {
        var response = await Exchange(TestCluster.Token("argo-workflow-sa"), new { tenantId = "acme" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("unknown_workload");
        AuditLines("refused").Should().ContainSingle(l => l.Contains("reason=unknown_workload") && l.Contains("sa=cho-workflows/argo-workflow-sa"));
    }

    [Fact]
    public async Task Registered_name_in_another_namespace_is_unknown()
    {
        var response = await Exchange(TestCluster.Token(Import, ns: "default"), new { tenantId = "acme" });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Import, "gamma")]
    [InlineData(Import, "ACME")]
    [InlineData(Import, "*")]
    [InlineData(Onboarding, "cho-platform")]
    [InlineData(Onboarding, "bad\"tenant")]
    public async Task Disallowed_tenant_is_refused(string serviceAccount, string tenant)
    {
        var response = await Exchange(TestCluster.Token(serviceAccount), new { tenantId = tenant });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("tenant_not_allowed");
        AuditLines("refused").Should().ContainSingle(l => l.Contains("reason=tenant_not_allowed") && l.Contains("client=" + serviceAccount));
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_tenant_is_a_bad_request()
    {
        (await Exchange(TestCluster.Token(Import), new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Exchange(TestCluster.Token(Import), null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Endpoint_is_absent_when_not_enabled()
    {
        using var plain = new TokenServiceFactory();
        var client = plain.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestCluster.Token(Import));
        var response = await client.PostAsJsonAsync(WorkloadTokenEndpoint.Path, new { tenantId = "acme" });
        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    // ── Under the shared ChoAuth layer ──────────────────────────────────

    [Fact]
    public async Task Issued_token_passes_a_permission_on_its_allow_list_and_fails_one_outside_it()
    {
        await using var service = await ChoServiceAsync();
        var client = Client(service, await IssueAsync(Import, "acme"));

        (await client.GetAsync("/perm/enrollment:process")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/perm/members:write")).StatusCode.Should().Be(HttpStatusCode.OK);
        // A service token (cho.service) would pass all of these.
        (await client.GetAsync("/perm/claims:read")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/perm/members:read")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/perm/settings:manage")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var whoami = await (await client.GetAsync("/whoami")).JsonAsync();
        whoami.GetProperty("sub").GetString().Should().Be(Import);
        whoami.GetProperty("tenant").GetString().Should().Be("acme");
        whoami.GetProperty("isService").GetBoolean().Should().BeFalse();

        // The tenant is the token's; a header naming another tenant is refused.
        using var spoof = new HttpRequestMessage(HttpMethod.Get, "/perm/enrollment:process");
        spoof.Headers.Add("X-Tenant-ID", "beta");
        (await client.SendAsync(spoof)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Workload_token_reaches_platform_routes_only_through_the_onboarding_service_client_route()
    {
        await using var service = await ChoServiceAsync();
        var onboarding = Client(service, await IssueAsync(Onboarding, "new-tenant"));
        var import = Client(service, await IssueAsync(Import, "acme"));

        foreach (var client in new[] { onboarding, import })
        {
            (await client.GetAsync("/perm/platform:tenants")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.GetAsync("/perm/platform:admin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.GetAsync("/svc/token-service")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await onboarding.GetAsync("/svc/onboarding")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await import.GetAsync("/svc/onboarding")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task User_token_issuer_cannot_forge_the_workload_marker()
    {
        await using var service = await ChoServiceAsync();

        // Signed with token-service's own key, as the user issuer: the same key
        // signs both, so only the issuer name tells them apart.
        var forged = Sign(_factory.SigningKeyPem, "cho-token-service", new Dictionary<string, object>
        {
            ["sub"] = Onboarding, ["azp"] = Onboarding, ["tenant_id"] = "new-tenant",
            ["roles"] = new[] { ChoWorkloadRole.Name },
            ["permissions"] = new[] { "settings:manage" },
            ["cho_workload_issuer"] = "true",
            ["cho_service_issuer"] = "true",
        });
        var client = Client(service, forged);

        (await client.GetAsync("/svc/onboarding")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var whoami = await (await client.GetAsync("/whoami")).JsonAsync();
        whoami.GetProperty("isWorkload").GetBoolean().Should().BeFalse();
        whoami.GetProperty("isService").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Service_token_issuer_cannot_present_a_workload_client_id()
    {
        await using var service = await ChoServiceAsync();

        // Every service holds the cho-internal key. A service token naming the
        // workflow's client id, or claiming the workload role, is not the workflow.
        var asService = Sign(InternalKeyPem, "cho-internal", new Dictionary<string, object>
        {
            ["sub"] = Onboarding, ["azp"] = Onboarding, ["tenant_id"] = "new-tenant",
            ["roles"] = new[] { ChoServiceRole.Name },
        });
        var asWorkload = Sign(InternalKeyPem, "cho-internal", new Dictionary<string, object>
        {
            ["sub"] = Onboarding, ["azp"] = Onboarding, ["tenant_id"] = "new-tenant",
            ["roles"] = new[] { ChoWorkloadRole.Name },
            ["permissions"] = new[] { "settings:manage" },
        });

        (await Client(service, asService).GetAsync("/svc/onboarding")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(service, asWorkload).GetAsync("/svc/onboarding")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The ordinary service token still works for its own client id.
        var tokenService = Sign(InternalKeyPem, "cho-internal", new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "token-service", ["tenant_id"] = "cho-platform",
            ["roles"] = new[] { ChoServiceRole.Name },
        });
        (await Client(service, tokenService).GetAsync("/svc/token-service")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Workload_issuer_cannot_mint_a_service_token()
    {
        await using var service = await ChoServiceAsync();
        var forged = Sign(_factory.SigningKeyPem, "cho-workload", new Dictionary<string, object>
        {
            ["sub"] = "token-service", ["azp"] = "token-service", ["tenant_id"] = "acme",
            ["roles"] = new[] { ChoServiceRole.Name, ChoWorkloadRole.Name },
        });
        var client = Client(service, forged);

        (await client.GetAsync("/svc/token-service")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/perm/claims:read")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Configuration ───────────────────────────────────────────────────

    [Theory]
    [InlineData("claims-service", "claims:read", "acme")]       // not a wf- client id
    [InlineData("wf-x", "platform:tenants", "acme")]            // platform permission
    [InlineData("wf-x", "claims:*", "acme")]                    // wildcard
    [InlineData("wf-x", "*:read", "acme")]                      // wildcard
    [InlineData("wf-x", "claims:read", "cho-platform")]         // reserved tenant
    public void Registration_that_would_widen_a_token_fails_startup(string clientId, string permission, string tenant)
    {
        var options = new WorkloadTokenOptions
        {
            Enabled = true,
            KubernetesIssuer = TestCluster.Issuer,
            JwksJson = TestCluster.Jwks(),
            Registrations =
            {
                new WorkloadRegistration
                {
                    Namespace = "cho-workflows", ServiceAccount = "wf-x", ClientId = clientId,
                    AllowedTenants = { tenant }, Permissions = { permission },
                },
            },
        };

        var act = () => options.Validate("cho-token-service", isDevelopment: false);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Workload_issuer_must_differ_from_the_user_issuer()
    {
        var options = new WorkloadTokenOptions
        {
            Enabled = true, KubernetesIssuer = TestCluster.Issuer, JwksJson = TestCluster.Jwks(), Issuer = "cho-token-service",
            Registrations = { new WorkloadRegistration { Namespace = "ns", ServiceAccount = "wf-x", ClientId = "wf-x", AllowedTenants = { "a" }, Permissions = { "claims:read" } } },
        };
        var act = () => options.Validate("cho-token-service", isDevelopment: false);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Shipped_registrations_validate_once_tenants_are_filled_in()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "src/services/token-service/k8s/token-service-deployment.yaml"));
        var start = manifest.IndexOf("registrations.json: |", StringComparison.Ordinal);
        var json = manifest[(start + "registrations.json: |".Length)..manifest.IndexOf("\n---", start, StringComparison.Ordinal)];
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(json, "<TENANT_[A-Z0-9_]+>", "tenant-a"));
            var options = new WorkloadTokenOptions
            {
                Enabled = true, KubernetesIssuer = "https://oidc.example/", RegistrationsFile = file,
            };
            options.Validate("cho-token-service", isDevelopment: false);

            var byClient = options.AllRegistrations().ToDictionary(r => r.ClientId!);
            byClient.Keys.Should().BeEquivalentTo("wf-enrollment-import", "wf-837-ingest", "wf-tenant-onboarding", "wf-277-rfai", "wf-278-ingest");
            byClient.Values.Should().OnlyContain(r => r.Namespace == "cho-workflows" && r.ServiceAccount == r.ClientId);
            byClient["wf-837-ingest"].Permissions.Should().Equal("claims:work");
            byClient["wf-tenant-onboarding"].AllowedTenants.Should().Equal("*");

            // As shipped, the placeholders are not tenant ids: startup fails until they are set.
            File.WriteAllText(file, json);
            ((Action)(() => options.Validate("cho-token-service", isDevelopment: false))).Should().Throw<InvalidOperationException>();
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

    // ── helpers ─────────────────────────────────────────────────────────

    private static readonly string InternalKeyPem = ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem();

    private static string PublicPem(string privatePem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);
        return ec.ExportSubjectPublicKeyInfoPem();
    }

    private static string Sign(string privatePem, string issuer, Dictionary<string, object> claims)
    {
        var ec = ECDsa.Create();
        ec.ImportFromPem(privatePem);
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "cho-api",
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(ec), SecurityAlgorithms.EcdsaSha256),
        });
    }

    private static HttpClient Client(WebApplication app, string token)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// A CHO backend service on the shared authentication, in Production mode,
    /// trusting the deployed issuers: token-service's user issuer, its workload
    /// issuer (same key, AllowWorkloadIdentity) and the cho-internal service issuer.
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
            ["ChoAuth:Issuers:1:Issuer"] = "cho-workload",
            ["ChoAuth:Issuers:1:PublicKeyPem"] = tokenServicePublic,
            ["ChoAuth:Issuers:1:AllowWorkloadIdentity"] = "true",
            ["ChoAuth:Issuers:2:Issuer"] = "cho-internal",
            ["ChoAuth:Issuers:2:PublicKeyPem"] = PublicPem(InternalKeyPem),
            ["ChoAuth:Issuers:2:AllowServiceRole"] = "true",
        });
        builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment);
        var app = builder.Build();
        app.UseChoAuthentication();

        foreach (var permission in new[]
                 {
                     "enrollment:process", "members:write", "members:read", "claims:read", "settings:manage",
                     "platform:tenants", "platform:admin",
                 })
        {
            app.MapGet("/perm/" + permission, () => Results.Ok())
                .RequireAuthorization(new RequirePermissionAttribute(permission));
        }
        app.MapGet("/svc/onboarding", () => Results.Ok())
            .RequireAuthorization(new RequireServiceClientAttribute(Onboarding));
        app.MapGet("/svc/token-service", () => Results.Ok())
            .RequireAuthorization(new RequireServiceClientAttribute("token-service"));
        app.MapGet("/whoami", (HttpContext http, ICurrentActor actor) => Results.Json(new
        {
            sub = actor.UserId,
            tenant = actor.TenantId,
            isService = actor.IsService,
            isWorkload = ChoPrincipal.IsWorkload(http.User),
        }))
        // A diagnostic endpoint for any authenticated caller, declared as such:
        // an unannotated minimal API in a service with no default permissions
        // is denied (the shared fallback policy).
        .RequireAuthorization();

        await app.StartAsync();
        return app;
    }
}
