using System.Net;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArService.Tests.Security;

/// <summary>
/// The real ar-service pipeline: no token means no tenant and no access. There
/// is no default tenant and no header fallback.
/// </summary>
public class ArPipelineAuthTests : IClassFixture<ArPipelineAuthTests.Factory>
{
    // The test project also references provider-contracts-service, which has its
    // own top-level Program, so the entry point is named through a controller type.
    public sealed class Factory : WebApplicationFactory<GlAccountsController>
    {
        public Mock<IGlAccountRepository> Accounts { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Program.cs reads this while building, so it goes in as a host setting.
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGlAccountRepository>();
                services.AddSingleton(Accounts.Object);
            });
        }
    }

    private readonly Factory _factory;

    public ArPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Accounts.Setup(r => r.SearchAsync(It.IsAny<GlAccountType?>(), It.IsAny<LineOfBusiness?>(),
                It.IsAny<GlAccountStatus?>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync(new List<GlAccount>());
    }

    [Fact]
    public async Task NoToken_IsRejected_NotServedAsDefaultTenant()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/ar/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync("/api/v1/ar/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TenantHeaderDisagreeingWithToken_IsForbidden()
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", ChoDevelopmentAuth.UserToken("tenant-1", ChoRolePermissions.TenantAdmin));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-2");

        var response = await client.GetAsync("/api/v1/ar/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ValidToken_IsServed()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync("/api/v1/ar/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RoleWithoutFinancePermission_IsForbidden()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("dev-user", ChoRolePermissions.MemberServices));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync("/api/v1/ar/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FinanceRole_IsServed()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler("dev-user", ChoRolePermissions.Finance));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-1");

        var response = await client.GetAsync("/api/v1/ar/accounts");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
