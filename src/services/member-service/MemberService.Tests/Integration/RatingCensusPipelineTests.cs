using System.Net;
using System.Net.Http.Headers;
using CloudHealthOffice.Infrastructure.Security;
using MemberService.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MemberService.Tests.Integration;

/// <summary>
/// The real pipeline for GET /api/v1/members/rating-census: premium-billing-service's
/// service token reads it; user tokens (Finance, MemberServices) and other services do not.
/// </summary>
public class RatingCensusPipelineTests : IClassFixture<MemberFhirSmokeTests.Factory>
{
    private const string Tenant = "tenant-census-int";
    private const string Path = "/api/v1/members/rating-census?groupNumber=GRP-CENSUS";

    private readonly WebApplicationFactory<Program> _host;

    public RatingCensusPipelineTests(MemberFhirSmokeTests.Factory factory)
    {
        if (!factory.MemberRepo.Members.Any(m => m.TenantId == Tenant))
            factory.MemberRepo.Members.Add(new Member
            {
                TenantId = Tenant, MemberId = "CEN-001", FirstName = "Test", LastName = "Census", GroupNumber = "GRP-CENSUS",
                IsSubscriber = true, RelationshipCode = "18", DateOfBirth = new DateTime(1980, 1, 1), EffectiveDate = new DateTime(2024, 1, 1)
            });
        _host = factory;
    }

    private HttpClient Service(string clientId)
    {
        var client = _host.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant));
        return client;
    }

    private HttpClient User(string role)
    {
        var client = _host.CreateDefaultClient(new ChoDevelopmentTokenHandler("user-" + role, role));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    [Fact]
    public async Task PremiumBillingServiceToken_ReadsTheCensus()
    {
        var response = await Service("premium-billing-service").GetAsync(Path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("CEN-001").And.NotContain("ssn", "no identifiers in the census");
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    public async Task UserTokens_AreRefused(string role)
    {
        (await User(role).GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OtherServiceTokens_AreRefused()
    {
        (await Service("capitation-service").GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
