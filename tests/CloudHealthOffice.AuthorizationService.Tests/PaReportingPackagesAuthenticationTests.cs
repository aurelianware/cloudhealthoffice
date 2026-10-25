using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class PaReportingPackagesAuthenticationTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly AuthorizationApiFactory _factory;
    public PaReportingPackagesAuthenticationTests(AuthorizationApiFactory factory) => _factory = factory;
    private static FreezePaReportingPackage Command() => new()
    {
        RequestKey = "key", PlanId = "plan", PlanScopeReference = "mapping:1", Year = 2025, TimeZone = "UTC",
        LineOfBusiness = LineOfBusiness.Medicaid, OutcomeCutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        KnowledgeCutoff = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };
    [Fact]
    public async Task AnonymousFreezeIsUnauthorized()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command())).StatusCode);
    }
    [Theory]
    [InlineData("authorizations:read")]
    [InlineData(PaReportingPackagesController.FreezePermission)]
    public async Task FreezeRequiresBothPermissions(string permission)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "actor", permission));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command())).StatusCode);
    }
    [Fact]
    public async Task RecorderComesFromToken_AndResponseCannotAuthorizePublication()
    {
        _factory.ReportingPackages.FreezeAsync(Arg.Any<FreezePaReportingPackage>(), "freeze-actor", Arg.Any<CancellationToken>())
            .Returns(new PaReportingPackage { Id = "frozen", CreatedBy = "freeze-actor" });
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "freeze-actor", "authorizations:read", PaReportingPackagesController.FreezePermission));
        var response = await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var package = await response.Content.ReadFromJsonAsync<PaReportingPackage>();
        Assert.False(package!.PublicationReady); Assert.False(package.PlanMappingVerified);
        Assert.Equal("freeze-actor", package.CreatedBy);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    [Fact]
    public async Task InfrastructureFailureIsExplicit503()
    {
        _factory.ReportingPackages.FreezeAsync(Arg.Any<FreezePaReportingPackage>(), "failure-actor", Arg.Any<CancellationToken>())
            .Returns<PaReportingPackage>(_ => throw new PaReportingPackageUnavailableException());
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "failure-actor", "authorizations:read", PaReportingPackagesController.FreezePermission));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command())).StatusCode);
    }
}
