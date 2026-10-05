using System.Net;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using EncounterService.Controllers;
using EncounterService.Models;
using EncounterService.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CloudHealthOffice.EncounterService.Tests.Security;

/// <summary>
/// The real encounter-service pipeline. The tenant comes only from a validated
/// CHO token: an X-Tenant-ID / X-Dev-Tenant-ID header on its own no longer
/// selects a tenant, a request with no tenant is never served as
/// "default-tenant", and encounters permissions are enforced.
/// </summary>
public class EncounterPipelineAuthTests : IClassFixture<EncounterPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<EncountersController>
    {
        public Mock<IEncounterRepository> Encounters { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads this while building, so it goes in as a host setting.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IEncounterRepository>();
                services.AddSingleton(Encounters.Object);
            });
        }
    }

    private const string ByIdPath = "/api/Encounters/enc-1";
    private readonly Factory _factory;

    /// <summary>The tenant the repository saw on each call (read from the request context, as the real repositories do).</summary>
    private readonly List<string?> _repoTenants = new();

    public EncounterPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Encounters.Reset();
        _factory.Encounters.Setup(r => r.GetByIdAsync("enc-1"))
            .ReturnsAsync(() =>
            {
                var tenant = CurrentTenant();
                _repoTenants.Add(tenant);
                return Existing(tenant ?? "");
            });
        _factory.Encounters.Setup(r => r.CreateAsync(It.IsAny<Encounter>()))
            .ReturnsAsync((Encounter e) =>
            {
                _repoTenants.Add(CurrentTenant());
                return e;
            });
        _factory.Encounters.Setup(r => r.UpdateAsync(It.IsAny<Encounter>())).ReturnsAsync((Encounter e) => e);
    }

    private string? CurrentTenant()
        => _factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items["TenantId"]?.ToString();

    private static Encounter Existing(string tenant) => new()
    {
        Id = "enc-1", TenantId = tenant, EncounterControlNumber = "ENC-1", ClaimId = "CLM-1",
        MemberId = "M1", BillingProviderNPI = "1234567890", PayerId = "PAYER1",
        LineOfBusiness = LineOfBusiness.Medicaid, Status = EncounterStatus.Rejected,
        ServiceDateFrom = DateTime.UtcNow.Date, ServiceDateTo = DateTime.UtcNow.Date,
        CreatedBy = "original-creator"
    };

    private HttpClient Client(string tenant, string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private static object NewEncounterBody() => new
    {
        encounterControlNumber = "ENC-NEW-1",
        claimId = "CLM-9",
        memberId = "M1",
        billingProviderNPI = "1234567890",
        payerId = "PAYER1",
        lineOfBusiness = "Medicaid",
        serviceDateFrom = DateTime.UtcNow.Date,
        serviceDateTo = DateTime.UtcNow.Date,
        serviceLines = new[]
        {
            new { lineNumber = 1, procedureCode = "99213", units = 1, chargeAmount = 100.00m,
                  serviceDateFrom = DateTime.UtcNow.Date, serviceDateTo = DateTime.UtcNow.Date }
        }
    };

    [Fact]
    public async Task NoToken_IsRejected_NotServedAsDefaultTenant()
    {
        var response = await _factory.CreateClient().GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _repoTenants.Should().BeEmpty("no request may reach the repository without a token, least of all as 'default-tenant'");
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Encounters.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DevTenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", "tenant-1");

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _factory.Encounters.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", ChoDevelopmentAuth.UserToken("tenant-1", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-2");

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _repoTenants.Should().NotContain("tenant-2");
    }

    [Fact]
    public async Task ValidToken_IsServedFromTokenTenant()
    {
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.TenantAdmin);

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _repoTenants.Should().Equal("tenant-1");
    }

    [Fact]
    public async Task HealthEndpoint_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RoleWithoutEncountersRead_IsForbidden()
    {
        // ClaimsExaminer holds neither encounters:read nor a *:read wildcard.
        var client = Client("tenant-1", "dev-user", ChoRolePermissions.ClaimsExaminer);

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Encounters.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(ChoRolePermissions.ComplianceOfficer)] // *:read
    [InlineData(ChoRolePermissions.Finance)]           // encounters:read
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]  // encounters:read + write
    public async Task RolesWithEncountersRead_CanRead(string role)
    {
        var client = Client("tenant-1", "auditor", role);

        var response = await client.GetAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("DELETE", "/api/Encounters/enc-1")]
    [InlineData("POST", "/api/Encounters/enc-1/resubmit")]
    [InlineData("POST", "/api/Encounters/batch/BATCH-1/submit")]
    public async Task ReadOnlyRole_CannotWrite(string method, string path)
    {
        foreach (var role in new[] { ChoRolePermissions.ComplianceOfficer, ChoRolePermissions.Finance })
        {
            var client = Client("tenant-1", "auditor", role);

            var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
        _factory.Encounters.Verify(r => r.UpdateAsync(It.IsAny<Encounter>()), Times.Never);
        _factory.Encounters.Verify(r => r.CreateAsync(It.IsAny<Encounter>()), Times.Never);
    }

    [Fact]
    public async Task ReadOnlyRole_CannotUpdateStatus()
    {
        var client = Client("tenant-1", "auditor", ChoRolePermissions.ComplianceOfficer);

        var response = await client.PutAsJsonAsync("/api/Encounters/enc-1/status", new { status = "Accepted" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Encounters.Verify(r => r.UpdateAsync(It.IsAny<Encounter>()), Times.Never);
    }

    [Fact]
    public async Task Submit_RecordsTokenSubjectAndTokenTenant_IgnoringBodyActorAndTenant()
    {
        var client = Client("tenant-1", "encounter-user-9", ChoRolePermissions.TenantAdmin);
        var body = new
        {
            tenantId = "tenant-2",
            createdBy = "someone-else",
            lastUpdatedBy = "someone-else",
            encounterControlNumber = "ENC-NEW-1",
            claimId = "CLM-9",
            memberId = "M1",
            billingProviderNPI = "1234567890",
            payerId = "PAYER1",
            lineOfBusiness = "Medicaid",
            serviceDateFrom = DateTime.UtcNow.Date,
            serviceDateTo = DateTime.UtcNow.Date,
            serviceLines = new[]
            {
                new { lineNumber = 1, procedureCode = "99213", units = 1, chargeAmount = 100.00m,
                      serviceDateFrom = DateTime.UtcNow.Date, serviceDateTo = DateTime.UtcNow.Date }
            }
        };

        var response = await client.PostAsJsonAsync("/api/Encounters", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _repoTenants.Should().Equal("tenant-1");
        _factory.Encounters.Verify(r => r.CreateAsync(It.Is<Encounter>(e =>
            e.CreatedBy == "encounter-user-9" && e.LastUpdatedBy == "encounter-user-9")), Times.Once);
    }

    [Fact]
    public async Task Submit_DoesNotRequireTenantInBody()
    {
        var client = Client("tenant-1", "encounter-user-9", ChoRolePermissions.TenantAdmin);

        var response = await client.PostAsJsonAsync("/api/Encounters", NewEncounterBody());

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
        _repoTenants.Should().Equal("tenant-1");
    }

    [Fact]
    public async Task Void_RecordsTokenSubject()
    {
        var client = Client("tenant-1", "encounter-user-9", ChoRolePermissions.TenantAdmin);

        var response = await client.DeleteAsync(ByIdPath);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Encounters.Verify(r => r.UpdateAsync(It.Is<Encounter>(e =>
            e.Status == EncounterStatus.Voided && e.LastUpdatedBy == "encounter-user-9")), Times.Once);
    }
}
