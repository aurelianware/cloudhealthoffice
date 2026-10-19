using System.Net;
using System.Text.Json;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Insights;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Authorization = AuthorizationService.Models.Authorization;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>
/// "Approved but unused": approved authorizations whose service period ended
/// in the period with no claim carrying their number yet.
/// </summary>
public class UnusedAuthorizationsTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static Authorization Approved(string number, int endedDaysAgo, LineOfBusiness lob = LineOfBusiness.Medicaid, bool useExpiration = false) => new()
    {
        Id = "id-" + number, AuthorizationNumber = number, Status = AuthorizationStatus.Approved, LineOfBusiness = lob,
        ApprovedServiceDateTo = useExpiration ? null : Now.AddDays(-endedDaysAgo),
        ExpirationDate = useExpiration ? Now.AddDays(-endedDaysAgo) : null,
    };

    private sealed class FakeUsage(ClaimsUsage usage) : IClaimsUsageClient
    {
        public IReadOnlyCollection<string>? Asked { get; private set; }
        public Task<ClaimsUsage> GetUsageAsync(IReadOnlyCollection<string> numbers, CancellationToken ct = default)
        {
            Asked = numbers;
            return Task.FromResult(usage);
        }
    }

    private static IAuthorizationRepository Repo(params Authorization[] auths)
    {
        var repo = Substitute.For<IAuthorizationRepository>();
        repo.ListApprovedEndingBetweenAsync(default, default).ReturnsForAnyArgs(auths.ToList());
        return repo;
    }

    [Fact]
    public async Task ExpiryDoesNotRemoveAnUnusedApprovalFromTheReport()
    {
        var authorization = Approved("PA-expired", 5);
        authorization.Status = AuthorizationStatus.Expired;
        authorization.ReviewDecision = "A1";
        var usage = new FakeUsage(new ClaimsUsage(new Dictionary<string, int>(), null));
        var result = await new UnusedAuthorizations(Repo(authorization), usage)
            .ComputeAsync(Now.AddDays(-30), Now, null, Now);
        Assert.Equal("PA-expired", Assert.Single(result.Unused!).AuthorizationNumber);
    }

    [Fact]
    public async Task Unused_are_ended_approvals_no_claim_carries()
    {
        var usage = new FakeUsage(new ClaimsUsage(new Dictionary<string, int> { ["PA-1"] = 3 }, null));
        var sut = new UnusedAuthorizations(Repo(
            Approved("PA-1", 5), Approved("PA-2", 5), Approved("PA-3", 10, useExpiration: true),
            Approved("PA-comm", 5, LineOfBusiness.Commercial), Approved("PA-future", -5)), usage);

        var result = await sut.ComputeAsync(Now.AddDays(-30), Now.AddDays(30), LineOfBusiness.Medicaid, Now);

        // Not yet ended (PA-future) and other lines of business are not counted, or asked about.
        Assert.Equal(new[] { "PA-1", "PA-2", "PA-3" }, result.Ended.Select(a => a.AuthorizationNumber).ToArray());
        Assert.Equal(new[] { "PA-2", "PA-3" }, result.Unused!.Select(a => a.AuthorizationNumber).ToArray());
        Assert.Equal(new[] { "PA-1", "PA-2", "PA-3" }, usage.Asked!.ToArray());
    }

    [Fact]
    public async Task When_claims_cant_be_checked_the_figure_is_unavailable_not_zero()
    {
        var sut = new UnusedAuthorizations(Repo(Approved("PA-1", 5)), new FakeUsage(ClaimsUsage.Unavailable("Claims Service couldn't be reached.")));

        var result = await sut.ComputeAsync(Now.AddDays(-30), Now, null, Now);

        Assert.Single(result.Ended);
        Assert.Null(result.Unused);
        Assert.Equal("Claims Service couldn't be reached.", result.UnavailableReason);
    }

    private sealed class StubHandler(HttpStatusCode code, string body = "[]") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static HttpClaimsUsageClient Client(StubHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpClaimsUsageClient.HttpClientName).Returns(new HttpClient(handler) { BaseAddress = new Uri("http://claims/") });
        return new HttpClaimsUsageClient(factory, NullLogger<HttpClaimsUsageClient>.Instance);
    }

    [Fact]
    public async Task A_role_without_claims_read_gets_a_reason_not_an_empty_answer()
    {
        var usage = await Client(new StubHandler(HttpStatusCode.Forbidden)).GetUsageAsync(["PA-1"]);

        Assert.Null(usage.Counts);
        Assert.Contains("can't read claims", usage.UnavailableReason);
    }

    [Fact]
    public async Task Numbers_are_sent_in_batches_of_two_thousand()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """[{"authorizationNumber":"PA-1","claimCount":2}]""");

        var usage = await Client(handler).GetUsageAsync(Enumerable.Range(0, 4500).Select(i => $"PA-{i}").ToList());

        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, usage.Counts!["PA-1"]);
    }
}

/// <summary>The unused figure and drill-down through the real pipeline.</summary>
public class UnusedAuthorizationsEndpointTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

    public UnusedAuthorizationsEndpointTests(AuthorizationApiFactory factory)
    {
        var now = DateTime.UtcNow;
        factory.AuthorizationRepository.ListActivityBetweenAsync(default, default).ReturnsForAnyArgs(new List<Authorization>());
        factory.AuthorizationRepository.GetOpenAuthorizationsAsync(default).ReturnsForAnyArgs(new List<Authorization>());
        factory.AuthorizationRepository.ListApprovedEndingBetweenAsync(default, default).ReturnsForAnyArgs(new List<Authorization>
        {
            new() { Id = "a1", AuthorizationNumber = "PA-1", Status = AuthorizationStatus.Approved, ApprovedServiceDateTo = now.AddDays(-3) },
            new() { Id = "a2", AuthorizationNumber = "PA-2", Status = AuthorizationStatus.Approved, ApprovedServiceDateTo = now.AddDays(-2) },
        });
        var usage = Substitute.For<IClaimsUsageClient>();
        usage.GetUsageAsync(default!, default).ReturnsForAnyArgs(new ClaimsUsage(new Dictionary<string, int> { ["PA-1"] = 1 }, null));
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IClaimsUsageClient>();
            s.AddScoped(_ => usage);
        }));
    }

    private HttpClient As(string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-" + role, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-unused");
        return client;
    }

    [Fact]
    public async Task The_figure_and_its_drill_down_agree()
    {
        using var insights = JsonDocument.Parse(await (await As(ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/insights")).Content.ReadAsStringAsync());
        var unused = insights.RootElement.GetProperty("unused");
        Assert.Equal(2, unused.GetProperty("ended").GetInt32());
        Assert.Equal(1, unused.GetProperty("withoutClaim").GetInt32());

        var list = await As(ChoRolePermissions.UMCoordinator).GetAsync("/api/authorizations/insights/list?scope=unused");
        using var rows = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal("a2", Assert.Single(rows.RootElement.EnumerateArray()).GetProperty("id").GetString());
    }
}
