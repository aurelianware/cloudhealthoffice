using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using ProviderService.Adapters;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// The real provider-service pipeline. Every caller needs a CHO token; the
/// tenant and the acting user come from that token, and the service's own
/// calls to tenant-service and provider-verification-service carry a token
/// (the caller's, or a service token for the tenant they name).
/// </summary>
public class ProviderPipelineAuthTests : IClassFixture<ProviderPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public Mock<IProviderRepository> Providers { get; } = new();
        public Mock<IProviderVersioningService> Versioning { get; } = new();
        public Mock<ICredentialingService> Credentialing { get; } = new();
        public Mock<IMpipRateService> Mpip { get; } = new();
        public Mock<IOrganizationService> Organizations { get; } = new();
        public Mock<INetworkRosterService> Roster { get; } = new();
        public CapturingHandler TenantServiceOutbound { get; } = new();
        public CapturingHandler VerificationOutbound { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads these while building, so they go in as host settings.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Services:TenantService", "http://tenant-service.cloudhealthoffice/api/v1");
            builder.UseSetting("ProviderVerification:BaseUrl", "http://provider-verification-service");
            builder.ConfigureServices(services =>
            {
                // Index initializers and the integrity worker would reach for Mongo.
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType?.Namespace?.StartsWith("ProviderService") == true)
                             .ToList())
                {
                    services.Remove(hosted);
                }

                services.RemoveAll<IProviderRepository>();
                services.AddSingleton(_ => Providers.Object);
                services.RemoveAll<IProviderVersioningService>();
                services.AddSingleton(_ => Versioning.Object);
                services.RemoveAll<ICredentialingService>();
                services.AddSingleton(_ => Credentialing.Object);
                services.RemoveAll<IMpipRateService>();
                services.AddSingleton(_ => Mpip.Object);
                services.RemoveAll<IOrganizationService>();
                services.AddSingleton(_ => Organizations.Object);
                services.RemoveAll<INetworkRosterService>();
                services.AddSingleton(_ => Roster.Object);
                // Provider reads look up the active bank account; keep it off Mongo.
                services.RemoveAll<IProviderBankAccountRepository>();
                services.AddSingleton<IProviderBankAccountRepository>(new Fakes.InMemoryProviderBankAccountRepository());

                services.AddHttpClient(ProviderTenantConfigCache.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => TenantServiceOutbound);
                services.AddHttpClient<IProviderVerificationClient, HttpProviderVerificationClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => VerificationOutbound);
            });
        }
    }

    public sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.NotFound;
        public string Body { get; set; } = "{}";

        public IReadOnlyList<HttpRequestMessage> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public HttpRequestMessage? Last => Requests.LastOrDefault();

        public void Reset()
        {
            lock (_requests) _requests.Clear();
            Status = HttpStatusCode.NotFound;
            Body = "{}";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string ProviderPath = "/api/v1/providers/p-1";
    private readonly Factory _factory;

    public ProviderPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Providers.Reset();
        _factory.Versioning.Reset();
        _factory.Credentialing.Reset();
        _factory.Mpip.Reset();
        _factory.Organizations.Reset();
        _factory.Roster.Reset();
        _factory.TenantServiceOutbound.Reset();
        _factory.VerificationOutbound.Reset();
        _factory.Services.GetRequiredService<ProviderTenantConfigCache>().Clear();

        _factory.Providers.Setup(r => r.GetByIdAsync("p-1"))
            .ReturnsAsync(new Provider
            {
                Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = "1234567890",
                ProviderType = ProviderType.Individual, CredentialingStatus = CredentialingStatus.Approved,
            });
        _factory.Versioning.Setup(v => v.CreateDraftAsync(It.IsAny<Provider>(), It.IsAny<string>()))
            .ReturnsAsync((Provider p, string _) => { p.ProviderId = "p-new"; p.VersionId = "v-1"; return p; });
        _factory.Credentialing.Setup(c => c.RecordDecisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RecordDecisionRequest>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialingEvent { Id = "evt-1" });
        _factory.Mpip.Setup(m => m.GetEnhancedRateMultiplierAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<int>()))
            .ReturnsAsync(1.0m);
        _factory.Organizations.Setup(o => o.GetByIdAsync("net-1"))
            .ReturnsAsync(new Organization { OrganizationId = "net-1", TenantId = Tenant });
        _factory.Roster.Setup(r => r.GetMembershipAsync(
                It.IsAny<string>(), "net-1", "1234567890", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NetworkMembershipResponse { NetworkId = "net-1", Npi = "1234567890", IsActiveMember = true });
    }

    private HttpClient Client(string tenant, string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    /// <summary>A token carrying exactly these permissions (no role expansion).</summary>
    private HttpClient ClientWithPermissions(string tenant, string subject, params string[] permissions)
    {
        var client = _factory.CreateDefaultClient();
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(subject, tenant, ["Custom"], permissions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static JwtSecurityToken Read(HttpRequestMessage sent)
    {
        sent.Headers.Authorization.Should().NotBeNull("CHO calls must carry a token");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        return new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization.Parameter);
    }

    // ── Inbound authentication and tenant ─────────────────────────────────

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync(ProviderPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        // Previously the local middleware took the tenant from this header.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync("/api/v1/providers/drafts", NewProviderBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Versioning.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DevTenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", Tenant);

        var response = await client.GetAsync(ProviderPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync(ProviderPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    // ── Permissions ───────────────────────────────────────────────────────

    [Fact]
    public async Task RoleWithProvidersRead_CanRead_AndPlatformLookupUsesTokenTenant()
    {
        var response = await Client(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer).GetAsync(ProviderPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var lookup = _factory.TenantServiceOutbound.Last;
        lookup.Should().NotBeNull();
        lookup!.RequestUri!.AbsolutePath.Should().EndWith($"/tenants/{Tenant}");
        // Inside a request the caller's own token is forwarded.
        Read(lookup).Subject.Should().Be("examiner-1");
    }

    [Fact]
    public async Task RoleWithoutProvidersPermission_IsForbidden()
    {
        // UMCoordinator holds no providers permission.
        var response = await Client(Tenant, "um-1", ChoRolePermissions.UMCoordinator).GetAsync(ProviderPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Providers.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadOnlyRole_CannotCreateProvider()
    {
        var response = await Client(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer)
            .PostAsJsonAsync("/api/v1/providers/drafts", NewProviderBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Versioning.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CredentialingDecision_NeedsProvidersCredential_NotJustProvidersWrite()
    {
        var client = ClientWithPermissions(Tenant, "writer-1", "providers:read", "providers:write");

        var response = await client.PostAsJsonAsync("/api/v1/providers/p-1/credentialing/decisions", DecisionBody());

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Credentialing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LegacyCredentialingPut_NeedsProvidersCredential()
    {
        var client = ClientWithPermissions(Tenant, "writer-1", "providers:read", "providers:write");

        var response = await client.PutAsJsonAsync("/api/v1/providers/p-1/credentialing",
            new { status = "Approved" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Credentialing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CredentialingDecision_RecordsTokenTenantAndSubject_IgnoringXUserId()
    {
        var client = Client(Tenant, "credentialer-7", ChoRolePermissions.ProviderRelations);
        client.DefaultRequestHeaders.Add("X-User-Id", "forged-user");

        var response = await client.PostAsJsonAsync("/api/v1/providers/p-1/credentialing/decisions", DecisionBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Credentialing.Verify(c => c.RecordDecisionAsync(
            Tenant, "p-1", It.IsAny<RecordDecisionRequest>(), "credentialer-7",
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NetworkMembership_IsReadableWithProvidersRead()
    {
        // claims-service checks membership during adjudication with an examiner's token.
        var response = await Client(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer)
            .GetAsync("/api/v1/networks/net-1/members/1234567890");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Roster.Verify(r => r.GetMembershipAsync(
            Tenant, "net-1", "1234567890", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NetworkWrite_NeedsNetworksWrite()
    {
        var client = ClientWithPermissions(Tenant, "writer-1", "providers:read", "providers:write", "networks:read");

        var response = await client.PostAsJsonAsync("/api/v1/networks", new { name = "Net" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Organizations.VerifyNoOtherCalls();
    }

    // ── Tenant and actor from the token on writes ─────────────────────────

    [Fact]
    public async Task CreateDraft_IgnoresBodyTenantAndAuditFields()
    {
        var body = NewProviderBody();
        body["tenantId"] = OtherTenant;
        body["createdBy"] = "forged-creator";
        body["lastUpdatedBy"] = "forged-updater";

        var response = await Client(Tenant, "relations-3", ChoRolePermissions.ProviderRelations)
            .PostAsJsonAsync("/api/v1/providers/drafts", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Versioning.Verify(v => v.CreateDraftAsync(
            It.Is<Provider>(p => p.TenantId == Tenant
                                 && p.CreatedBy == "relations-3"
                                 && p.LastUpdatedBy == "relations-3"),
            "relations-3"), Times.Once);
    }

    [Fact]
    public async Task CreateDraft_DoesNotNeedTenantInBody()
    {
        var response = await Client(Tenant, "relations-3", ChoRolePermissions.ProviderRelations)
            .PostAsJsonAsync("/api/v1/providers/drafts", NewProviderBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Versioning.Verify(v => v.CreateDraftAsync(
            It.Is<Provider>(p => p.TenantId == Tenant), "relations-3"), Times.Once);
    }

    // ── MPIP path tenant ──────────────────────────────────────────────────

    [Fact]
    public async Task MpipRateCheck_ForAnotherTenantInPath_IsForbidden()
    {
        var response = await Client(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer)
            .GetAsync($"/api/mpip/{OtherTenant}/rate-check?providerId=p-1&serviceDate=2026-01-01&memberAge=10");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Mpip.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MpipRateCheck_ForTokenTenant_IsServed()
    {
        var response = await Client(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer)
            .GetAsync($"/api/mpip/{Tenant}/rate-check?providerId=p-1&serviceDate=2026-01-01&memberAge=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Mpip.Verify(m => m.GetEnhancedRateMultiplierAsync("p-1", Tenant, It.IsAny<DateTime>(), 10), Times.Once);
    }

    [Fact]
    public async Task MpipQualificationWrite_ForAnotherTenantInPath_IsForbidden()
    {
        var response = await Client(Tenant, "relations-3", ChoRolePermissions.ProviderRelations)
            .PutAsJsonAsync($"/api/mpip/{OtherTenant}/providers/p-1",
                new { npi = "1234567890", qualificationPeriod = "2026-2027", providerId = "p-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Mpip.VerifyNoOtherCalls();
    }

    // ── Outbound calls to other CHO services ──────────────────────────────

    [Fact]
    public async Task TenantServiceCall_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        // ProviderTenantConfigCache is a singleton that can be reached with no
        // inbound request; tenant-service requires a CHO token, so the factory
        // client must mint a service token for the tenant being looked up.
        var cache = _factory.Services.GetRequiredService<ProviderTenantConfigCache>();

        await cache.GetAsync(Tenant);

        var sent = _factory.TenantServiceOutbound.Last;
        sent.Should().NotBeNull();
        var token = Read(sent!);
        token.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
        token.Subject.Should().Be("provider-service");
    }

    [Fact]
    public async Task TenantServiceRefusal_FailsTheRequest_InsteadOfServingTheDefaultPlatform()
    {
        _factory.TenantServiceOutbound.Status = HttpStatusCode.Unauthorized;

        var response = await Client(Tenant, "examiner-1", ChoRolePermissions.ClaimsExaminer).GetAsync(ProviderPath);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        _factory.Providers.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerificationServiceCall_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        // The integrity-projection worker calls provider-verification-service
        // with no inbound request.
        _factory.VerificationOutbound.Status = HttpStatusCode.OK;
        _factory.VerificationOutbound.Body = """{"count":0,"results":[]}""";
        using var scope = _factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IProviderVerificationClient>();

        await client.VerifyBatchAsync(Tenant, ["1234567890"]);

        var sent = _factory.VerificationOutbound.Last;
        sent.Should().NotBeNull();
        var token = Read(sent!);
        token.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
        token.Subject.Should().Be("provider-service");
    }

    private static Dictionary<string, object?> NewProviderBody() => new()
    {
        ["npi"] = "1234567890",
        ["providerType"] = "Individual",
        ["firstName"] = "Ada",
        ["lastName"] = "Lovelace",
        ["primarySpecialty"] = "Internal Medicine",
        ["taxonomyCode"] = "207R00000X",
    };

    private static object DecisionBody() => new
    {
        decision = "Approved",
        decisionAuthorityType = "DelegatedAuthority",
        decisionAuthorityId = "medical-director-1",
    };
}
