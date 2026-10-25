using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class PaReportingPackagesAuthenticationTests : IClassFixture<AuthorizationApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command(), Json)).StatusCode);
    }
    [Theory]
    [InlineData("authorizations:read")]
    [InlineData(PaReportingPackagesController.FreezePermission)]
    public async Task FreezeRequiresBothPermissions(string permission)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "actor", permission));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command(), Json)).StatusCode);
    }
    [Theory]
    [InlineData("frozen")]
    [InlineData("frozen/inputs")]
    public async Task PrivateReadsRequireFreezePermission(string suffix)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "reader", "authorizations:read"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/authorizations/cms-0057/packages/" + suffix)).StatusCode);
    }

    [Fact]
    public async Task RecorderComesFromToken_AndResponseCannotAuthorizePublication()
    {
        _factory.ReportingPackages.FreezeAsync(Arg.Any<FreezePaReportingPackage>(), "freeze-actor", Arg.Any<CancellationToken>())
            .Returns(new PaReportingPackage { Id = "frozen", CreatedBy = "freeze-actor" });
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "freeze-actor", "authorizations:read", PaReportingPackagesController.FreezePermission));
        var response = await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command(), Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var package = await response.Content.ReadFromJsonAsync<PaReportingPackage>(Json);
        Assert.False(package!.PublicationReady); Assert.False(package.PlanMappingVerified);
        Assert.Equal("freeze-actor", package.CreatedBy);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    [Fact]
    public async Task AuthorizedPrivateHeaderAndInputs_AreAuditedAndNotCached()
    {
        _factory.ReportingPackages.GetAsync("audited", Arg.Any<CancellationToken>()).Returns(new PaReportingPackage { Id = "audited" });
        _factory.ReportingPackages.ReadInputsAsync("audited", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PaReportingPackageInput>());
        var log = new RecordAccessStoreInMemory();
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IRecordAccessStore>();
            services.AddSingleton<IRecordAccessStore>(log);
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "audit-reader", "authorizations:read", PaReportingPackagesController.FreezePermission));
        foreach (var path in new[] { "audited", "audited/inputs" })
        {
            var response = await client.GetAsync("/api/authorizations/cms-0057/packages/" + path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Contains(log.All, e => e.ActorId == "audit-reader" && e.RecordId == "audited" && e.RecordType == "AuthorizationReportingPackage" && e.Action == "Read");
        Assert.Contains(log.All, e => e.ActorId == "audit-reader" && e.RecordId == "audited" && e.Action == "Inputs");
    }

    [Fact]
    public async Task InfrastructureFailureIsExplicit503()
    {
        _factory.ReportingPackages.FreezeAsync(Arg.Any<FreezePaReportingPackage>(), "failure-actor", Arg.Any<CancellationToken>())
            .Returns<PaReportingPackage>(_ => throw new PaReportingPackageUnavailableException());
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.IssueTokenWithPermissions("tenant", "failure-actor", "authorizations:read", PaReportingPackagesController.FreezePermission));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/api/authorizations/cms-0057/packages", Command(), Json)).StatusCode);
    }
}
