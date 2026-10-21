using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Finding a claim by the attachment control number its 837 PWK06 announced
/// (how a 275's TRN02 finds its claim), on a real mongod.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ClaimRepositoryAttachmentControlNumberTests : IAsyncLifetime
{
    private const string Tenant = "tenant-pwk";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public ClaimRepositoryAttachmentControlNumberTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_pwk_test");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private IClaimRepository Repo(string tenant = Tenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = tenant;
        return new ClaimRepositoryMongo(_database, new HttpContextAccessor { HttpContext = ctx },
            NullLogger<ClaimRepositoryMongo>.Instance);
    }

    private static Claim ClaimWith(string id, string tenant, string? controlNumber, string? chain = null, int version = 1) => new()
    {
        Id = id,
        ClaimVersionId = chain ?? id,
        VersionNumber = version,
        TenantId = tenant,
        ClaimNumber = "CN-" + (chain ?? id),
        MemberId = "M1",
        BillingProviderNPI = "1234567893",
        AttachmentReports = controlNumber is null
            ? []
            : [new ClaimAttachmentReport { ReportTypeCode = "09", TransmissionCode = "EL", ControlNumber = controlNumber }],
    };

    [Fact]
    public async Task FindsTheClaim_InTheTenantOnly_AndOnlyByAnExactNumber()
    {
        await Repo().CreateAsync(ClaimWith("c1", Tenant, "ACN-77"));
        await Repo().CreateAsync(ClaimWith("c2", Tenant, "ACN-770"));
        await Repo("other").CreateAsync(ClaimWith("c3", "other", "ACN-77"));
        await Repo().CreateAsync(ClaimWith("c4", Tenant, null));

        var found = await Repo().FindByAttachmentControlNumberAsync("ACN-77", 20);

        found.Select(c => c.Id).Should().Equal("c1");
    }

    [Fact]
    public async Task AnswersOneClaimPerVersionChain_ItsLatestVersion()
    {
        await Repo().CreateAsync(ClaimWith("c1-v1", Tenant, "ACN-9", chain: "c1", version: 1));
        await Repo().CreateAsync(ClaimWith("c1-v2", Tenant, "ACN-9", chain: "c1", version: 2));

        var found = await Repo().FindByAttachmentControlNumberAsync("ACN-9", 20);

        found.Select(c => c.Id).Should().Equal("c1-v2");
    }
}
