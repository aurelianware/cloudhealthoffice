using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SponsorService.Models;
using SponsorService.Repositories;

namespace SponsorService.Tests.Security;

/// <summary>
/// The real sponsor-service pipeline with the repository replaced. Every caller
/// needs a CHO token; the tenant and the acting user come from it. Reads need
/// enrollment:read, writes enrollment:process; the compact member view also
/// admits members:read.
/// </summary>
public class SponsorPipelineAuthTests : IClassFixture<SponsorPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<ISponsorRepository> Repository { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["CosmosDb:Endpoint"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISponsorRepository>();
                services.AddSingleton(Repository.Object);
            });
        }
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "sponsor-user-7";
    private const string Group = "GRP-100";
    private readonly Factory _factory;

    public SponsorPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        var repo = _factory.Repository;
        repo.Reset();
        repo.Setup(r => r.GetByGroupNumberAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((string tenant, string group) => new Sponsor
            {
                TenantId = tenant,
                GroupNumber = group,
                EmployerName = "Acme Co",
                Status = SponsorStatus.Active
            });
        repo.Setup(r => r.GetPagedAsync(It.IsAny<string>(), It.IsAny<SponsorStatus?>(), It.IsAny<bool>(),
                It.IsAny<LineOfBusiness?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync((new List<Sponsor>(), (string?)null, 0));
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        repo.Setup(r => r.CreateAsync(It.IsAny<Sponsor>())).ReturnsAsync((Sponsor s) => s);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Sponsor>())).ReturnsAsync((Sponsor s) => s);
    }

    private HttpClient Client(string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    private HttpClient BearerClient(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static object NewSponsor => new
    {
        groupNumber = Group,
        employerName = "Acme Co",
        effectiveDate = "2026-01-01T00:00:00Z",
        createdBy = "someone-else"
    };

    private static object Suspend => new { status = "Suspended" };

    private void NothingRan() => _factory.Repository.VerifyNoOtherCalls();

    private void NoWrites()
    {
        _factory.Repository.Verify(r => r.CreateAsync(It.IsAny<Sponsor>()), Times.Never);
        _factory.Repository.Verify(r => r.UpdateAsync(It.IsAny<Sponsor>()), Times.Never);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var response = await _factory.CreateClient().GetAsync("/api/v1/sponsors");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        NothingRan();
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        // Before: the local TenantMiddleware took the tenant from this header
        // with no authentication, so anyone could read and change any tenant's
        // sponsors.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var list = await client.GetAsync("/api/v1/sponsors");
        var get = await client.GetAsync($"/api/v1/sponsors/{Group}");
        var create = await client.PostAsJsonAsync("/api/v1/sponsors", NewSponsor);
        var update = await client.PutAsJsonAsync($"/api/v1/sponsors/{Group}", Suspend);
        var terminate = await client.DeleteAsync($"/api/v1/sponsors/{Group}");

        list.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        create.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        update.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        terminate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        NothingRan();
    }

    [Fact]
    public async Task DevTenantHeaderWithoutToken_IsRejected()
    {
        // Before: X-Dev-Tenant-ID was accepted in every environment.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/v1/sponsors/{Group}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        NothingRan();
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = BearerClient(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync($"/api/v1/sponsors/{Group}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NothingRan();
    }

    [Fact]
    public async Task TokenTenant_IsTheTenantUsed()
    {
        var client = BearerClient(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.TenantAdmin));

        var response = await client.GetAsync($"/api/v1/sponsors/{Group}?tenantId={OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Repository.Verify(r => r.GetByGroupNumberAsync(Tenant, Group), Times.Once);
        _factory.Repository.Verify(r => r.GetByGroupNumberAsync(OtherTenant, It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Create_CreatedByIsTokenSubject_NotTheBodyField()
    {
        // Before: CreatedBy was User.Identity.Name ?? "System". The local
        // middleware never authenticated anyone, so every sponsor said "System".
        var response = await Client(Tenant, ChoRolePermissions.EnrollmentSpecialist)
            .PostAsJsonAsync("/api/v1/sponsors", NewSponsor);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Repository.Verify(r => r.CreateAsync(It.Is<Sponsor>(s =>
            s.TenantId == Tenant && s.GroupNumber == Group && s.CreatedBy == User)), Times.Once);
    }

    [Fact]
    public async Task Update_LastUpdatedByIsTokenSubject()
    {
        var response = await Client(Tenant, ChoRolePermissions.EnrollmentSpecialist)
            .PutAsJsonAsync($"/api/v1/sponsors/{Group}", Suspend);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Repository.Verify(r => r.UpdateAsync(It.Is<Sponsor>(s =>
            s.TenantId == Tenant && s.Status == SponsorStatus.Suspended && s.LastUpdatedBy == User)), Times.Once);
    }

    [Fact]
    public async Task Terminate_RecordsTokenSubject()
    {
        // Before: termination recorded no actor at all.
        var response = await Client(Tenant, ChoRolePermissions.EnrollmentSpecialist)
            .DeleteAsync($"/api/v1/sponsors/{Group}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.Repository.Verify(r => r.UpdateAsync(It.Is<Sponsor>(s =>
            s.TenantId == Tenant && s.Status == SponsorStatus.Terminated && s.LastUpdatedBy == User)), Times.Once);
    }

    [Theory]
    [InlineData(ChoRolePermissions.MemberServices)]   // members:read, no enrollment permission
    [InlineData(ChoRolePermissions.ProviderRelations)] // neither
    public async Task RoleWithoutEnrollmentPermissions_CannotReadOrChangeSponsors(string role)
    {
        var client = Client(Tenant, role);

        var list = await client.GetAsync("/api/v1/sponsors");
        var get = await client.GetAsync($"/api/v1/sponsors/{Group}");
        var summary = await client.GetAsync($"/api/v1/sponsors/{Group}/coverage-summary");
        var create = await client.PostAsJsonAsync("/api/v1/sponsors", NewSponsor);
        var update = await client.PutAsJsonAsync($"/api/v1/sponsors/{Group}", Suspend);
        var terminate = await client.DeleteAsync($"/api/v1/sponsors/{Group}");

        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        get.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        summary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        terminate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NothingRan();
    }

    [Fact]
    public async Task MemberServices_CanReadTheMemberView()
    {
        // The portal's Member Details dialog shows this to member services staff.
        var response = await Client(Tenant, ChoRolePermissions.MemberServices)
            .GetAsync($"/api/v1/sponsors/{Group}/member-view");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Repository.Verify(r => r.GetByGroupNumberAsync(Tenant, Group), Times.Once);
    }

    [Fact]
    public async Task Finance_ReadsSponsorsForBilling_ButCannotChangeThem()
    {
        // Finance holds billing:read, not enrollment:read.
        var client = Client(Tenant, ChoRolePermissions.Finance);

        var list = await client.GetAsync("/api/v1/sponsors");
        var get = await client.GetAsync($"/api/v1/sponsors/{Group}");
        var summary = await client.GetAsync($"/api/v1/sponsors/{Group}/coverage-summary");
        var create = await client.PostAsJsonAsync("/api/v1/sponsors", NewSponsor);
        var update = await client.PutAsJsonAsync($"/api/v1/sponsors/{Group}", Suspend);
        var terminate = await client.DeleteAsync($"/api/v1/sponsors/{Group}");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        list.StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized);
        summary.StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        terminate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NoWrites();
    }

    [Fact]
    public async Task ProviderRelations_CannotReadTheMemberView()
    {
        var response = await Client(Tenant, ChoRolePermissions.ProviderRelations)
            .GetAsync($"/api/v1/sponsors/{Group}/member-view");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NothingRan();
    }

    [Fact]
    public async Task ReadOnlyRole_CanReadSponsors_ButCannotChangeThem()
    {
        // ComplianceOfficer holds *:read only.
        var client = Client(Tenant, ChoRolePermissions.ComplianceOfficer);

        var get = await client.GetAsync($"/api/v1/sponsors/{Group}");
        var create = await client.PostAsJsonAsync("/api/v1/sponsors", NewSponsor);
        var update = await client.PutAsJsonAsync($"/api/v1/sponsors/{Group}", Suspend);
        var terminate = await client.DeleteAsync($"/api/v1/sponsors/{Group}");

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        terminate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NoWrites();
    }

    [Fact]
    public async Task EnrollmentImportServiceToken_CanCheckAndCreateSponsors()
    {
        // enrollment-import-service checks and creates sponsors from a
        // background import, with a service token minted for the file's tenant.
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("enrollment-import-service", Tenant);
        var client = BearerClient(token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);

        var exists = await client.GetAsync($"/api/v1/sponsors/{Group}");
        var create = await client.PostAsJsonAsync("/api/v1/sponsors", NewSponsor);

        exists.StatusCode.Should().Be(HttpStatusCode.OK);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Repository.Verify(r => r.CreateAsync(It.Is<Sponsor>(s =>
            s.TenantId == Tenant && s.CreatedBy == "enrollment-import-service")), Times.Once);
    }

    [Fact]
    public async Task HealthProbe_NeedsNoToken()
    {
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }
}
