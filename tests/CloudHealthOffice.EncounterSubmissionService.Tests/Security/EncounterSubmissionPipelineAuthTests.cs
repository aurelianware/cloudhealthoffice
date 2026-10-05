using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using EncounterSubmissionService.Controllers;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.Services;
using EncounterSubmissionService.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Security;

/// <summary>
/// The real encounter-submission-service pipeline. Every caller needs a CHO
/// token; the tenant comes from that token, and the <c>{tenantId}</c> route
/// segment can only echo it, never select another tenant.
/// </summary>
public class EncounterSubmissionPipelineAuthTests : IClassFixture<EncounterSubmissionPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<EncounterSubmissionController>
    {
        public Mock<IEncounterSubmissionService> Service { get; } = new();
        public CapturingHandler ClaimsOutbound { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads this while building, so it goes in as a host setting.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Kafka:BootstrapServers", "");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEncounterSubmissionService>();
                services.AddSingleton(_ => Service.Object);

                // The deadline worker would start batching against the mocks.
                var worker = services.Single(d => d.ImplementationType == typeof(EncounterSubmissionWorker));
                services.Remove(worker);

                services.AddHttpClient("ClaimsService")
                    .ConfigurePrimaryHttpMessageHandler(() => ClaimsOutbound);
            });
        }
    }

    public sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private readonly Factory _factory;

    public EncounterSubmissionPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Service.Reset();
        _factory.Service.Setup(s => s.GetPendingSubmissionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(Array.Empty<EncounterSubmission>());
        _factory.Service.Setup(s => s.GetStatusSummaryAsync(It.IsAny<string>()))
            .ReturnsAsync((string t) => new EncounterStatusSummary { TenantId = t });
        _factory.Service.Setup(s => s.RetrySubmissionAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string id, string t) => new EncounterSubmission { Id = id, TenantId = t });
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("encounter-user-7", roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static object Ack => new { batchId = "batch-1", content = "ISA*00~AK9*A*1*1*1~" };

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync($"/api/encounters/{Tenant}/pending");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var response = await client.PostAsJsonAsync($"/api/encounters/{Tenant}/acknowledge", Ack);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/encounters/{OtherTenant}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidToken_IsServedFromTokenTenant()
    {
        var response = await Client(Tenant).GetAsync($"/api/encounters/{Tenant}/pending?page=2&pageSize=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Service.Verify(s => s.GetPendingSubmissionsAsync(Tenant, 2, 10), Times.Once);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("summary")]
    [InlineData("deadline-warnings")]
    public async Task PathTenantOtherThanTokenTenant_IsForbiddenForReads(string resource)
    {
        var response = await Client(Tenant).GetAsync($"/api/encounters/{OtherTenant}/{resource}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PathTenantOtherThanTokenTenant_CannotAcknowledgeBatch()
    {
        var response = await Client(Tenant).PostAsJsonAsync($"/api/encounters/{OtherTenant}/acknowledge", Ack);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Service.Verify(s => s.ProcessAcknowledgmentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task PathTenantOtherThanTokenTenant_CannotRetrySubmission()
    {
        var response = await Client(Tenant).PostAsync($"/api/encounters/{OtherTenant}/retry/sub-1", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Service.Verify(s => s.RetrySubmissionAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Acknowledge_AppliesToTokenTenant()
    {
        var response = await Client(Tenant).PostAsJsonAsync($"/api/encounters/{Tenant}/acknowledge", Ack);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Service.Verify(s => s.ProcessAcknowledgmentAsync("batch-1", It.IsAny<string>(), Tenant), Times.Once);
    }

    [Fact]
    public async Task RoleWithoutEncountersRead_IsForbidden()
    {
        // ClaimsExaminer holds no encounters permission.
        var response = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync($"/api/encounters/{Tenant}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadOnlyRole_CanRead()
    {
        // ComplianceOfficer holds *:read, so encounters:read but not encounters:write.
        var response = await Client(Tenant, ChoRolePermissions.ComplianceOfficer).GetAsync($"/api/encounters/{Tenant}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Service.Verify(s => s.GetStatusSummaryAsync(Tenant), Times.Once);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.Finance)] // encounters:read only
    public async Task ReadOnlyRole_CannotAcknowledgeOrRetry(string role)
    {
        var client = Client(Tenant, role);

        var ack = await client.PostAsJsonAsync($"/api/encounters/{Tenant}/acknowledge", Ack);
        var retry = await client.PostAsync($"/api/encounters/{Tenant}/retry/sub-1", null);

        ack.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        retry.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task RolesWithEncountersRead_CanReadSummary(string role)
    {
        var response = await Client(Tenant, role).GetAsync($"/api/encounters/{Tenant}/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ClaimsServiceCall_WithoutCaller_CarriesServiceTokenForNamedTenant()
    {
        // The deadline worker and Kafka consumer call claims-service with no
        // inbound request; the factory client must mint a service token.
        var http = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("ClaimsService");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/claims/claim-1");
        request.Headers.Add("X-Tenant-ID", Tenant);

        await http.SendAsync(request);

        var sent = _factory.ClaimsOutbound.Last;
        sent.Should().NotBeNull();
        sent!.Headers.Authorization.Should().NotBeNull();
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        var token = new JwtSecurityTokenHandler().ReadJwtToken(sent.Headers.Authorization.Parameter);
        token.Claims.Should().Contain(c => c.Type == ChoClaimTypes.TenantId && c.Value == Tenant);
        token.Subject.Should().Be("encounter-submission-service");
    }
}
