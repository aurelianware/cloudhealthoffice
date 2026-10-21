using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.TokenService.ServiceTokens;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CloudHealthOffice.TokenService.Tests;

/// <summary>
/// <c>POST /v1/token/service</c> with <c>ServiceTokens:IdentityProvider=Kubernetes</c>:
/// a service proves its identity with its projected service-account token (GKE,
/// AKS, EKS or on premises) instead of an Entra workload identity.
/// </summary>
public sealed class KubernetesServiceTokenTests : IDisposable
{
    private const string ServiceNamespace = "cloudhealthoffice";

    public static Dictionary<string, string?> Settings() => new()
    {
        ["ServiceTokens:Enabled"] = "true",
        ["ServiceTokens:IdentityProvider"] = "Kubernetes",
        ["ServiceTokens:Kubernetes:KubernetesIssuer"] = TestCluster.Issuer,
        ["ServiceTokens:Kubernetes:JwksJson"] = TestCluster.Jwks(),
        ["ServiceTokens:Kubernetes:Audience"] = "cho-token-service",
        ["ServiceTokens:Issuer"] = "cho-token-service-svc",
        ["ServiceTokens:TokenLifetime"] = "00:05:00",
        ["ServiceTokens:ServiceClients:0:ClientId"] = "claims-service",
        ["ServiceTokens:ServiceClients:0:Namespace"] = ServiceNamespace,
        ["ServiceTokens:ServiceClients:0:ServiceAccount"] = "claims-service",
        ["ServiceTokens:ServiceClients:1:ClientId"] = "capitation-service",
        ["ServiceTokens:ServiceClients:1:Namespace"] = ServiceNamespace,
        ["ServiceTokens:ServiceClients:1:ServiceAccount"] = "capitation-service",
    };

    private readonly TokenServiceFactory _factory = new() { ExtraSettings = Settings() };

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> Exchange(string? saToken, string tenant = "acme")
    {
        var client = _factory.CreateClient();
        if (saToken != null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saToken);
        return await client.PostAsJsonAsync(ServiceTokenEndpoint.Path, new { tenantId = tenant });
    }

    private IEnumerable<string> AuditLines(string outcome)
        => _factory.Logs.Entries
            .Where(e => e.Category == "CloudHealthOffice.TokenService.Audit" && e.Message.Contains("CHO service token " + outcome))
            .Select(e => e.Message);

    [Fact]
    public async Task Registered_service_account_gets_a_service_token_naming_its_own_client_id()
    {
        var response = await Exchange(TestCluster.Token("claims-service", ns: ServiceNamespace));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.JsonAsync();
        body.GetProperty("client_id").GetString().Should().Be("claims-service");
        body.GetProperty("tenant_id").GetString().Should().Be("acme");

        var jwt = new JsonWebToken(body.GetProperty("access_token").GetString());
        jwt.Issuer.Should().Be("cho-token-service-svc");
        jwt.Subject.Should().Be("claims-service");
        jwt.GetClaim("azp").Value.Should().Be("claims-service");
        jwt.GetClaim("tenant_id").Value.Should().Be("acme");
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value).Should().Equal(ChoServiceRole.Name);

        // The same claim shape as an Entra-issued or locally minted service token, so callees do not change.
        var local = new JsonWebToken(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("claims-service", "acme"));
        jwt.Claims.Select(c => c.Type).Distinct().Should().BeEquivalentTo(local.Claims.Select(c => c.Type).Distinct());

        AuditLines("issued").Should().ContainSingle(l =>
            l.Contains("client=claims-service") && l.Contains($"sa={ServiceNamespace}/claims-service")
            && l.Contains("pod=claims-service-pod-1") && l.Contains("tenant=acme"));
    }

    [Fact]
    public async Task A_service_account_can_only_obtain_tokens_naming_itself()
    {
        var capitation = await (await Exchange(TestCluster.Token("capitation-service", ns: ServiceNamespace))).JsonAsync();
        capitation.GetProperty("client_id").GetString().Should().Be("capitation-service");
    }

    [Fact]
    public async Task Unregistered_service_account_is_forbidden_and_audited()
    {
        var response = await Exchange(TestCluster.Token("member-service", ns: ServiceNamespace));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.JsonAsync()).GetProperty("error").GetString().Should().Be("unknown_service");
        AuditLines("refused").Should().ContainSingle(l =>
            l.Contains("reason=unknown_service") && l.Contains($"sa={ServiceNamespace}/member-service"));
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task The_same_service_account_name_in_another_namespace_is_forbidden()
    {
        var response = await Exchange(TestCluster.Token("claims-service", ns: "other-team"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("wrong_audience")]
    [InlineData("wrong_issuer")]
    [InlineData("other_key")]
    [InlineData("not_pod_bound")]
    [InlineData("expired")]
    public async Task Invalid_service_account_tokens_are_unauthorized(string variant)
    {
        var token = variant switch
        {
            // A token projected for the CHO API (or any other audience) is not a proof for token-service.
            "wrong_audience" => TestCluster.Token("claims-service", ns: ServiceNamespace, audience: "cho-api"),
            "wrong_issuer" => TestCluster.Token("claims-service", ns: ServiceNamespace, issuer: "https://other-cluster.example/"),
            "other_key" => TestCluster.Token("claims-service", ns: ServiceNamespace,
                key: new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = TestCluster.Key.KeyId }),
            "not_pod_bound" => TestCluster.Token("claims-service", ns: ServiceNamespace, podBound: false),
            _ => TestCluster.Token("claims-service", ns: ServiceNamespace, expires: DateTime.UtcNow.AddMinutes(-10)),
        };

        var response = await Exchange(token);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AuditLines("issued").Should().BeEmpty();
    }

    [Fact]
    public async Task An_entra_token_is_not_accepted_when_the_provider_is_kubernetes()
    {
        var response = await Exchange(TestWorkloadIdentity.Token(TestWorkloadIdentity.ClaimsOid));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_proof_is_unauthorized()
    {
        var response = await Exchange(null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Configuration ───────────────────────────────────────────────────

    private static ServiceTokenOptions ValidOptions() => new()
    {
        Enabled = true,
        IdentityProvider = ServiceIdentityProvider.Kubernetes,
        Kubernetes = new KubernetesServiceIdentityOptions
        {
            KubernetesIssuer = "https://container.googleapis.com/v1/projects/cho/locations/us-west1/clusters/cho",
        },
        ServiceClients =
        {
            new ServiceClientRegistration { ClientId = "claims-service", Namespace = ServiceNamespace, ServiceAccount = "claims-service" },
        },
    };

    [Fact]
    public void A_valid_kubernetes_configuration_passes()
        => ValidOptions().Validate("cho-token-service", "cho-workload", isDevelopment: false);

    [Fact]
    public void Kubernetes_needs_an_issuer()
    {
        var options = ValidOptions();
        options.Kubernetes.KubernetesIssuer = "";
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*KubernetesIssuer*");
    }

    [Fact]
    public void Discovery_must_be_https_outside_development()
    {
        var options = ValidOptions();
        options.Kubernetes.KubernetesIssuer = "http://cluster.internal";
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*https*");
    }

    [Fact]
    public void The_projected_audience_cannot_be_the_cho_api()
    {
        var options = ValidOptions();
        options.Kubernetes.Audience = ChoDevelopmentAuth.Audience;
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Audience*");
    }

    [Fact]
    public void Entra_registrations_are_refused_with_the_kubernetes_provider()
    {
        var options = ValidOptions();
        options.ServiceClients[0].ObjectId = TestWorkloadIdentity.ClaimsOid;
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Entra identities*");
    }

    [Fact]
    public void Kubernetes_registrations_are_refused_with_the_entra_provider()
    {
        var options = new ServiceTokenOptions
        {
            Enabled = true,
            EntraTenantId = Ids.PlatformDirectory,
            Audiences = { TestWorkloadIdentity.ServiceApiAppIdUri },
            JwksJson = TestWorkloadIdentity.Jwks(),
            ServiceClients =
            {
                new ServiceClientRegistration
                {
                    ClientId = "claims-service", ObjectId = TestWorkloadIdentity.ClaimsOid,
                    Namespace = ServiceNamespace, ServiceAccount = "claims-service",
                },
            },
        };
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*Kubernetes identities*");
    }

    [Theory]
    [InlineData("Claims_Service", "claims-service")]
    [InlineData("cloudhealthoffice", "")]
    [InlineData("", "claims-service")]
    public void Registrations_need_a_valid_namespace_and_service_account(string ns, string sa)
    {
        var options = ValidOptions();
        options.ServiceClients[0].Namespace = ns;
        options.ServiceClients[0].ServiceAccount = sa;
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_service_account_cannot_be_two_services()
    {
        var options = ValidOptions();
        options.ServiceClients.Add(new ServiceClientRegistration
        {
            ClientId = "capitation-service", Namespace = ServiceNamespace, ServiceAccount = "claims-service",
        });
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false)))
            .Should().Throw<InvalidOperationException>().WithMessage("*registered twice*");
    }

    [Fact]
    public void Workload_client_ids_are_never_services()
    {
        var options = ValidOptions();
        options.ServiceClients[0].ClientId = "wf-nightly";
        ((Action)(() => options.Validate("cho-token-service", "cho-workload", false))).Should().Throw<InvalidOperationException>();
    }
}
