using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Testing.Mongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Claims Insights against a real mongod. Every figure must equal the count
/// of its drill-down search (one definition per metric), current versions
/// only, inside the tenant.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public class ClaimInsightsTests : IAsyncLifetime
{
    private const string Tenant = "tenant-insights";
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddDays(-30);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public ClaimInsightsTests(MongoRunnerFixture mongo) => _mongo = mongo;

    private ClaimRepositoryMongo Repo(string tenant = Tenant)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["TenantId"] = tenant;
        return new ClaimRepositoryMongo(_database, new HttpContextAccessor { HttpContext = ctx }, NullLogger<ClaimRepositoryMongo>.Instance);
    }

    private Task Seed(string id, ClaimStatus status, int ageDays, Action<Claim>? shape = null, string tenant = Tenant)
    {
        var claim = new Claim
        {
            Id = id, TenantId = tenant, ClaimNumber = "CN-" + id, MemberId = "M1", BillingProviderNPI = "1234567890",
            ClaimType = ClaimType.Professional, Status = status, TotalChargeAmount = 100m,
            LineOfBusiness = LineOfBusiness.Medicaid,
            ServiceDateFrom = Now.AddDays(-ageDays - 2), ServiceDateTo = Now.AddDays(-ageDays - 2),
            SubmittedDate = Now.AddDays(-ageDays),
        };
        shape?.Invoke(claim);
        // Inserted directly: the claim's lifecycle state is the fixture, not what CreateAsync would set.
        return _database.GetCollection<Claim>("Claims").InsertOneAsync(claim);
    }

    public async Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_insights");

        // Open inventory: ages 3, 20, 20, 50, 90.
        await Seed("o1", ClaimStatus.Received, 3);
        await Seed("o2", ClaimStatus.InAdjudication, 20, c => c.TotalChargeAmount = 250.50m);
        await Seed("o3", ClaimStatus.Pended, 20, c => c.PendDetails = new PendDetails { PendCode = "AUTH", PendedAt = Now.AddDays(-19) });
        await Seed("o4", ClaimStatus.Pended, 50, c => c.PendDetails = new PendDetails { PendCode = "AUTH", PendedAt = Now.AddDays(-45) });
        await Seed("o5", ClaimStatus.Pended, 90);

        // Adjudicated in the period: received 10 days ago, adjudicated after 2 / 4 / 6 / 8 days.
        await Seed("a1", ClaimStatus.Paid, 10, c => { c.AdjudicatedDate = Now.AddDays(-8); c.AdjudicationResult = new AdjudicationResult { PayerPayment = 80m }; });
        await Seed("a2", ClaimStatus.Approved, 10, c => { c.AdjudicatedDate = Now.AddDays(-6); c.AdjudicationResult = new AdjudicationResult { PayerPayment = 70m }; });
        await Seed("a3", ClaimStatus.Denied, 10, c =>
        {
            c.AdjudicatedDate = Now.AddDays(-4);
            c.AdjudicationResult = new AdjudicationResult { DenialReasonCode = "27", DenialReason = "Coverage terminated" };
            c.ExaminerResolutions = [new ExaminerResolutionRecord()];
        });
        await Seed("a4", ClaimStatus.Deny, 10, c => { c.AdjudicatedDate = Now.AddDays(-2); c.AdjudicationResult = new AdjudicationResult { DenialReasonCode = "27" }; });

        // Not counted: adjudicated before the period, superseded, voided, another tenant.
        await Seed("x-old", ClaimStatus.Paid, 60, c => c.AdjudicatedDate = Now.AddDays(-40));
        await Seed("x-superseded", ClaimStatus.Pended, 20, c => c.SupersededAt = Now.AddDays(-1));
        await Seed("x-void", ClaimStatus.Voided, 10, c => c.AdjudicatedDate = Now.AddDays(-5));
        await Seed("x-tenant", ClaimStatus.Pended, 20, tenant: "tenant-other");
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private Task<ClaimInsights> Insights() => Repo().GetInsightsAsync(From, Now, null, Now);

    private async Task<int> DrillDown(ClaimCriteria criteria) => (await Repo().SearchByCriteriaAsync(criteria, 1, 100)).TotalCount;

    [Fact]
    public async Task Inventory_counts_open_current_claims_by_status_and_sums_charges()
    {
        var inventory = (await Insights()).Inventory;

        inventory.Total.Should().Be(5);
        inventory.TotalCharge.Should().Be(650.50m);
        inventory.ByStatus.Single(s => s.Status == ClaimStatus.Pended).Count.Should().Be(3);
        (await DrillDown(new ClaimCriteria { Open = true })).Should().Be(inventory.Total);
    }

    [Fact]
    public async Task Aging_buckets_match_their_drill_down()
    {
        var aging = (await Insights()).Inventory.Aging;

        aging.Select(a => (a.Label, a.Count)).Should().Equal(
            ("0–15 days", 1), ("16–30 days", 2), ("31–45 days", 0), ("46–60 days", 1), ("61+ days", 1));
        foreach (var bucket in aging)
            (await DrillDown(new ClaimCriteria { Open = true, ReceivedAfter = bucket.ReceivedAfter, ReceivedOnOrBefore = bucket.ReceivedOnOrBefore }))
                .Should().Be(bucket.Count, bucket.Label);
    }

    [Fact]
    public async Task Pend_reasons_group_by_code_with_the_oldest_pend()
    {
        var reasons = (await Insights()).PendReasons;

        reasons.Select(r => (r.PendCode, r.Count)).Should().Equal(("AUTH", 2), (ClaimCriteria.UnspecifiedPendCode, 1));
        reasons[0].OldestPendedAt.Should().Be(Now.AddDays(-45));
        (await DrillDown(new ClaimCriteria { PendCode = "AUTH" })).Should().Be(2);
        (await DrillDown(new ClaimCriteria { PendCode = ClaimCriteria.UnspecifiedPendCode })).Should().Be(1);
    }

    [Fact]
    public async Task Outcomes_count_claims_adjudicated_in_the_period_and_the_auto_rate()
    {
        var outcomes = (await Insights()).Outcomes;

        outcomes.Adjudicated.Should().Be(4);
        outcomes.AutoAdjudicated.Should().Be(3);
        outcomes.AutoAdjudicationRate.Should().Be(0.75m);
        outcomes.TotalPaid.Should().Be(150m);
        (await DrillDown(new ClaimCriteria { Adjudicated = true, AdjudicatedFrom = From, AdjudicatedBefore = Now })).Should().Be(4);
        (await DrillDown(new ClaimCriteria { Adjudicated = true, AdjudicatedFrom = From, AdjudicatedBefore = Now, AutoAdjudicated = false })).Should().Be(1);
    }

    [Fact]
    public async Task Denial_reasons_cover_both_denied_phases()
    {
        var denials = (await Insights()).DenialReasons;

        var top = denials.Should().ContainSingle().Subject;
        top.Code.Should().Be("27");
        top.Count.Should().Be(2);
        top.Description.Should().Be("Coverage terminated");
        (await DrillDown(new ClaimCriteria { DenialReasonCode = "27", AdjudicatedFrom = From, AdjudicatedBefore = Now })).Should().Be(2);
    }

    [Fact]
    public async Task Turnaround_is_receipt_to_adjudication()
    {
        var t = (await Insights()).Turnaround;

        t.Count.Should().Be(4);
        t.AverageDays.Should().Be(5m);
        t.MedianDays.Should().Be(4m);
        t.P90Days.Should().Be(8m);
    }

    [Fact]
    public async Task Another_tenant_sees_only_its_own_claims()
    {
        var other = await Repo("tenant-other").GetInsightsAsync(From, Now, null, Now);

        other.Inventory.Total.Should().Be(1);
        other.Outcomes.Adjudicated.Should().Be(0);
        other.Outcomes.AutoAdjudicationRate.Should().BeNull();
    }

    [Fact]
    public async Task Line_of_business_narrows_everything()
    {
        var commercial = await Repo().GetInsightsAsync(From, Now, LineOfBusiness.Commercial, Now);

        commercial.Inventory.Total.Should().Be(0);
        commercial.Outcomes.Adjudicated.Should().Be(0);
    }
}
