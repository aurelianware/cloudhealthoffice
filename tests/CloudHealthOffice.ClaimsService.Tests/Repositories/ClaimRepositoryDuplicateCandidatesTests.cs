using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Mongo-backed coverage for <see cref="ClaimRepositoryMongo.FindDuplicateCandidatesAsync"/>
/// — the candidate query behind DuplicateClaimStage. Verifies the tenant /
/// member / service-window scoping and that dead rows and the claim's own
/// version chain are filtered server-side.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class ClaimRepositoryDuplicateCandidatesTests : IAsyncLifetime
{
    private const string Tenant = "tenant-dup";
    private const string Member = "M1";

    private static readonly DateTime Dos = new(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private IMongoCollection<Claim> _collection = null!;
    private ClaimRepositoryMongo _repo = null!;

    public ClaimRepositoryDuplicateCandidatesTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_dup_candidates_test");
        _collection = _database.GetCollection<Claim>("Claims");
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = Tenant;
        _repo = new ClaimRepositoryMongo(
            _database,
            new HttpContextAccessor { HttpContext = ctx },
            NullLogger<ClaimRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static Claim Row(
        string id,
        string? chain = null,
        string tenant = Tenant,
        string member = Member,
        DateTime? dos = null,
        ClaimStatus status = ClaimStatus.Paid,
        ClaimVersionState state = ClaimVersionState.Paid,
        string frequency = "1") => new()
    {
        Id = id,
        ClaimVersionId = chain ?? id,
        VersionNumber = 1,
        TenantId = tenant,
        ClaimNumber = "CN-" + id,
        MemberId = member,
        BillingProviderNPI = "1234567890",
        Status = status,
        VersionState = state,
        ClaimFrequencyCode = frequency,
        ServiceDateFrom = dos ?? Dos,
        ServiceDateTo = dos ?? Dos,
        SubmittedDate = Dos.AddDays(1),
    };

    [Fact]
    public async Task Returns_only_live_overlapping_claims_for_tenant_and_member_outside_own_chain()
    {
        var superseded = Row("superseded", state: ClaimVersionState.Submitted, status: ClaimStatus.Approved);
        superseded.SupersededAt = Dos.AddDays(3);

        await _collection.InsertManyAsync(new[]
        {
            Row("live-paid"),
            Row("live-pended", status: ClaimStatus.Pended, state: ClaimVersionState.Submitted),
            Row("other-tenant", tenant: "tenant-other"),
            Row("other-member", member: "M2"),
            Row("other-dos", dos: Dos.AddDays(5)),
            Row("denied", status: ClaimStatus.Denied, state: ClaimVersionState.Denied),
            Row("voided", status: ClaimStatus.Voided, state: ClaimVersionState.Voided),
            Row("adjusted", state: ClaimVersionState.Adjusted),
            Row("draft", status: ClaimStatus.Submitted, state: ClaimVersionState.Draft),
            Row("void-request", frequency: "8", status: ClaimStatus.Approved, state: ClaimVersionState.Adjudicated),
            superseded,
            // The claim under adjudication, an earlier version of its chain,
            // and a legacy row whose Id is the chain key.
            Row("self", chain: "chain-x", status: ClaimStatus.Submitted, state: ClaimVersionState.Submitted),
            Row("self-v1", chain: "chain-x"),
            Row("chain-x", chain: string.Empty),
        });

        var result = await _repo.FindDuplicateCandidatesAsync(Tenant, Member, Dos, Dos, "chain-x");

        result.Select(c => c.Id).Should().BeEquivalentTo(new[] { "live-paid", "live-pended" });
    }

    [Fact]
    public async Task Includes_multi_day_prior_whose_service_period_overlaps_window()
    {
        var stay = Row("inpatient-stay");
        stay.ServiceDateFrom = Dos.AddDays(-3);
        stay.ServiceDateTo = Dos.AddDays(2);
        await _collection.InsertOneAsync(stay);

        var result = await _repo.FindDuplicateCandidatesAsync(Tenant, Member, Dos, Dos, "chain-x");

        result.Should().ContainSingle(c => c.Id == "inpatient-stay");
    }
}
