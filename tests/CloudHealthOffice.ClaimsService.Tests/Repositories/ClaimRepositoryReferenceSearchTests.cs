using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Claim search by claim number and by prior authorization number (claim
/// header or any line, 837 REF*G1) against a real mongod: only the claims
/// that carry the reference, inside the tenant.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class ClaimRepositoryReferenceSearchTests : IAsyncLifetime
{
    private const string Tenant = "tenant-ref";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public ClaimRepositoryReferenceSearchTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_ref_search");

        await Seed("c-header", Tenant, c => c.PriorAuthorizationNumber = "PA-1");
        await Seed("c-line", Tenant, c => c.ClaimLines = [new ClaimLine { LineNumber = 1, PriorAuthorizationNumber = "PA-1" }]);
        await Seed("c-other", Tenant, c => c.PriorAuthorizationNumber = "PA-2");
        await Seed("c-none", Tenant);
        await Seed("c-other-tenant", "tenant-x", c => c.PriorAuthorizationNumber = "PA-1");
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private ClaimRepositoryMongo Repo(string tenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = tenant;
        return new ClaimRepositoryMongo(_database, new HttpContextAccessor { HttpContext = ctx }, NullLogger<ClaimRepositoryMongo>.Instance);
    }

    private Task Seed(string id, string tenant, Action<Claim>? shape = null)
    {
        var claim = new Claim
        {
            Id = id,
            TenantId = tenant,
            ClaimNumber = "CN-" + id,
            MemberId = "M1",
            BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional,
            Status = ClaimStatus.Submitted,
            TotalChargeAmount = 100m,
            ServiceDateFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            ServiceDateTo = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            SubmittedDate = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc),
        };
        shape?.Invoke(claim);
        return Repo(tenant).CreateAsync(claim);
    }

    [Fact]
    public async Task An_authorization_number_finds_claims_carrying_it_on_the_header_or_a_line()
    {
        var (page, total) = await Repo(Tenant).SearchByCriteriaAsync(new ClaimCriteria { AuthorizationNumber = " PA-1 " }, 1, 25);

        page.Select(c => c.Id).Should().BeEquivalentTo(["c-header", "c-line"]);
        total.Should().Be(2);
    }

    [Fact]
    public async Task A_claim_number_finds_only_that_claim()
    {
        var (page, total) = await Repo(Tenant).SearchByCriteriaAsync(new ClaimCriteria { ClaimNumber = "CN-c-other" }, 1, 25);

        page.Should().ContainSingle().Which.Id.Should().Be("c-other");
        total.Should().Be(1);
    }

    [Fact]
    public async Task Other_filters_still_narrow_the_result()
    {
        var (page, _) = await Repo(Tenant).SearchByCriteriaAsync(new ClaimCriteria { AuthorizationNumber = "PA-1", MemberId = "M-nobody" }, 1, 25);

        page.Should().BeEmpty();
    }
}
