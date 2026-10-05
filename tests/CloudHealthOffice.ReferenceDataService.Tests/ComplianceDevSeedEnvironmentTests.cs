using CloudHealthOffice.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceDataService.Controllers;
using ReferenceDataService.Models;
using ReferenceDataService.Repositories;
using Xunit;

namespace CloudHealthOffice.ReferenceDataService.Tests;

/// <summary>
/// dev-seed is available on Development and test hosts only. The shared auth
/// and the test hosts call the test environment "Testing"; the controller
/// accepted only "Test", so seeding on a Testing host was refused.
/// </summary>
public sealed class ComplianceDevSeedEnvironmentTests
{
    private const string Tenant = "tenant-1";

    private static ComplianceConfigController Controller(string environmentName, InMemoryComplianceConfigRepository repository)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.TenantId).Returns(Tenant);
        actor.SetupGet(a => a.UserId).Returns("seed-user");
        actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        return new ComplianceConfigController(
            new MemoryCache(new MemoryCacheOptions()), repository, env.Object, actor.Object,
            NullLogger<ComplianceConfigController>.Instance);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Testing")]
    public async Task DevSeed_IsAvailableOnDevelopmentAndTestHosts(string environmentName)
    {
        var repository = new InMemoryComplianceConfigRepository();

        var result = await Controller(environmentName, repository)
            .DevSeedConfig(Tenant, new TenantComplianceConfig { StateCode = "FL" });

        result.Result.Should().BeOfType<OkObjectResult>();
        (await repository.GetAsync(Tenant))!.CreatedBy.Should().Be("seed-user");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task DevSeed_IsRefusedElsewhere(string environmentName)
    {
        var repository = new InMemoryComplianceConfigRepository();

        var result = await Controller(environmentName, repository)
            .DevSeedConfig(Tenant, new TenantComplianceConfig { StateCode = "FL" });

        result.Result.Should().BeOfType<ForbidResult>();
        (await repository.GetAsync(Tenant)).Should().BeNull();
    }
}
