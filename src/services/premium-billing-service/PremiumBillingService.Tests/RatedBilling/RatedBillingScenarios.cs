using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Rating;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using static PremiumBillingService.Tests.Rating.RatingFixtures;

namespace PremiumBillingService.Tests.RatedBilling;

/// <summary>
/// Rated invoice generation end to end against a real store and the real
/// repositories, once per backend (<see cref="RatedBillingMongoTests"/> on a
/// mongod, <see cref="RatedBillingCosmosEmulatorTests"/> on the Cosmos DB
/// emulator). coverage-service and member-service are in-memory fakes holding
/// synthetic members; every assertion re-reads what was saved.
/// </summary>
public abstract class RatedBillingScenarios : IAsyncLifetime
{
    protected const string Tenant = "tenant-rated";
    protected const string Group = "GRP-R1";

    protected IPremiumInvoiceRepository Invoices = null!;
    protected IRateTableRepository RateTables = null!;
    protected readonly FakeCoverage Coverage = new();
    protected readonly FakeCensus Census = new();
    protected readonly FixedClock Clock = new(D(2026, 2, 20));
    protected RatedBillingOptions Options = null!;
    private RequestContext _http = null!;

    protected abstract Task<(IPremiumInvoiceRepository Invoices, IRateTableRepository RateTables)> CreateStoresAsync(IHttpContextAccessor http);

    public abstract Task DisposeAsync();

    public async Task InitializeAsync()
    {
        _http = new RequestContext { HttpContext = new DefaultHttpContext() };
        _http.HttpContext.Items["TenantId"] = Tenant;
        (Invoices, RateTables) = await CreateStoresAsync(_http);
        Options = new RatedBillingOptions { Tenants = { [Tenant] = new RatedBillingTenantOptions { Enabled = true } } };
    }

    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    public sealed class FixedClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    public sealed class FakeCoverage : ICoverageServiceClient
    {
        public List<CoverageDto> Records { get; } = new();

        public Task<List<CoverageDto>> GetActiveCoveragesByGroupAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("rated billing must list all coverage, terminated included");

        public Task<List<CoverageDto>> GetAllCoveragesByGroupAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(Records.Where(r => r.GroupNumber == groupNumber).Select(Copy).ToList());

        private static CoverageDto Copy(CoverageDto c) => new()
        {
            CoverageId = c.CoverageId, MemberId = c.MemberId, GroupNumber = c.GroupNumber, PlanId = c.PlanId,
            InsuranceLineCode = c.InsuranceLineCode, EffectiveDate = c.EffectiveDate, TerminationDate = c.TerminationDate
        };
    }

    public sealed class FakeCensus : IMemberServiceClient
    {
        public List<RatingCensusMemberDto> Members { get; } = new();

        public Task<List<RatingCensusMemberDto>> GetRatingCensusAsync(string tenantId, string groupNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(Members.ToList());
    }

    protected RatedInvoiceGenerator Generator() => new(Invoices, RateTables, Coverage, Census,
        Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<RatedInvoiceGenerator>.Instance);

    protected RateTableService RateTableService() => new(RateTables, Clock);

    protected static SponsorDto Sponsor => new() { GroupNumber = Group, EmployerName = "Synthetic Employer", BillingDay = 1, GracePeriodDays = 30 };

    protected Task<RatedInvoiceOutcome> Generate(DateTime period) =>
        Generator().GenerateAsync(Sponsor, Tenant, period, "run-1", "billing-user");

    protected async Task<RateTableRecord> SaveTierTable(decimal ee = 500m, DateTime? to = null, int? expected = null)
    {
        var table = TierTable();
        table.TierRates!.EmployeeOnly = ee;
        table.EffectiveTo = to ?? PlanYearEnd;
        return await RateTableService().SaveAsync(new SaveRateTableRequest { Table = table, ExpectedCurrentVersion = expected }, "actuary-1");
    }

    protected void Enroll(string coverageId, string memberId, DateTime effective, DateTime? term = null)
    {
        Coverage.Records.Add(new CoverageDto
        {
            CoverageId = coverageId, MemberId = memberId, GroupNumber = Group, PlanId = "PPO-GOLD",
            InsuranceLineCode = "HLT", EffectiveDate = effective, TerminationDate = term
        });
        if (Census.Members.All(m => m.MemberId != memberId))
            Census.Members.Add(new RatingCensusMemberDto
            {
                MemberId = memberId, IsSubscriber = true, RelationshipCode = "18", FirstName = "Test", LastName = memberId,
                DateOfBirth = D(1985, 5, 5)
            });
    }

    protected async Task<List<PremiumInvoice>> GroupInvoices() => (await Invoices.GetByGroupNumberAsync(Group)).ToList();

    // ── Idempotency and immutability ───────────────────────────────────

    [SkippableFact]
    public async Task Generation_IsIdempotent_AnIssuedInvoiceIsNeverDuplicatedOrChanged()
    {
        var rates = await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));

        var first = await Generate(D(2026, 3, 1));
        first.Kind.Should().Be(RatedInvoiceOutcomeKind.Created);
        first.Invoice.Status.Should().Be(InvoiceStatus.Generated);

        // Coverage changes after the invoice was issued; re-running must not touch it.
        Enroll("cov-B", "B", D(2026, 3, 1));
        var second = await Generate(D(2026, 3, 1));

        second.Kind.Should().Be(RatedInvoiceOutcomeKind.AlreadyIssued);
        var stored = (await GroupInvoices()).Should().ContainSingle().Subject;
        stored.Id.Should().Be(first.Invoice.Id);
        stored.TotalAmount.Should().Be(500m);
        stored.PricingSource.Should().Be(PricingSource.RatingEngine);
        stored.RatingRevision.Should().Be(1);
        stored.LineItems.Should().ContainSingle().Which.Should().Match<InvoiceLineItem>(l =>
            l.RateTableId == rates.RateTableId && l.RateTableVersion == rates.Version && l.RateTableHash == rates.ContentHash
            && l.RatingSegments!.Count == 1);
    }

    [SkippableFact]
    public async Task ConcurrentGenerations_CreateOneInvoice()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Generate(D(2026, 3, 1)))));

        outcomes.Count(o => o.Kind == RatedInvoiceOutcomeKind.Created).Should().Be(1);
        outcomes.Should().OnlyContain(o => o.Kind == RatedInvoiceOutcomeKind.Created || o.Kind == RatedInvoiceOutcomeKind.AlreadyIssued);
        (await GroupInvoices()).Should().ContainSingle();
    }

    [SkippableFact]
    public async Task MissingRate_KeepsTheInvoiceInDraft_ThenRegeneratingInPlaceIssuesIt()
    {
        // Rates end in February: March cannot be rated.
        var february = await SaveTierTable(to: D(2026, 2, 28));
        Enroll("cov-A", "A", D(2026, 1, 1));

        var draft = await Generate(D(2026, 3, 1));

        draft.Invoice.Status.Should().Be(InvoiceStatus.Draft);
        draft.Invoice.LineItems.Should().BeEmpty("a missing rate is never a $0 line");
        draft.Invoice.RatingExceptions.Should().ContainSingle().Which.Should().Match<InvoiceRatingException>(e =>
            e.Code == InvoiceCalculationIssue.RateNotFound && e.CoverageId == "cov-A" && e.ServiceMonth == D(2026, 3, 1));
        (await Invoices.GetOverdueAsync()).Should().BeEmpty();

        // The draft can be neither issued nor paid while the exception stands.
        var issue = () => Generator().IssueDraftAsync(draft.Invoice.Id, "billing-user");
        await issue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rating exception*");

        // The actuary extends the table (version 2); regenerating the draft rates March and issues it.
        var v2 = await SaveTierTable(to: PlanYearEnd, expected: february.Version);
        var regenerated = await Generator().RegenerateDraftAsync(draft.Invoice.Id, Tenant, "billing-user");

        regenerated.Kind.Should().Be(RatedInvoiceOutcomeKind.Regenerated);
        var stored = (await GroupInvoices()).Should().ContainSingle().Subject;
        stored.Id.Should().Be(draft.Invoice.Id);
        stored.Status.Should().Be(InvoiceStatus.Generated);
        stored.RatingExceptions.Should().BeEmpty();
        stored.RatingRevision.Should().Be(2);
        stored.LineItems.Single().Should().Match<InvoiceLineItem>(l => l.TotalPremium == 500m && l.RateTableVersion == v2.Version);

        var again = () => Generator().RegenerateDraftAsync(draft.Invoice.Id, Tenant, "billing-user");
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never changed*");
    }

    [SkippableFact]
    public async Task HeldForReview_StaysDraftUntilIssued()
    {
        Options.Tenants[Tenant].HoldForReview = true;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));

        var draft = await Generate(D(2026, 3, 1));
        draft.Invoice.Status.Should().Be(InvoiceStatus.Draft);
        draft.Invoice.RatingExceptions.Should().BeEmpty();

        var issued = await Generator().IssueDraftAsync(draft.Invoice.Id, "billing-user");

        issued.Status.Should().Be(InvoiceStatus.Generated);
        issued.IssuedAt.Should().Be(Clock.Now);
        (await Invoices.GetByIdAsync(draft.Invoice.Id))!.Status.Should().Be(InvoiceStatus.Generated);
        (await Generate(D(2026, 3, 1))).Kind.Should().Be(RatedInvoiceOutcomeKind.AlreadyIssued);
    }

    // ── Retro changes land on the next invoice ─────────────────────────

    [SkippableFact]
    public async Task RetroTerm_IsCreditedOnTheNextInvoice_TheIssuedOneIsUnchanged()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        Enroll("cov-B", "B", D(2026, 1, 1));
        var february = (await Generate(D(2026, 2, 1))).Invoice;
        february.TotalAmount.Should().Be(1000m);

        // B's termination on 2/14 arrives after February was issued (28 days: due 500 × 14/28 = 250, credit −250).
        Coverage.Records.Single(c => c.CoverageId == "cov-B").TerminationDate = D(2026, 2, 14);
        Clock.Now = D(2026, 2, 25);
        var march = (await Generate(D(2026, 3, 1))).Invoice;

        march.LineItems.Should().ContainSingle(l => l.CoverageId == "cov-A");
        march.Adjustments.Should().ContainSingle().Which.Should().Match<InvoiceAdjustment>(a =>
            a.Type == AdjustmentType.RetroTerm && a.CoverageId == "cov-B" && a.Amount == -250m && a.ServicePeriodStart == D(2026, 2, 1));
        march.TotalAmount.Should().Be(250m);

        var storedFebruary = await Invoices.GetByIdAsync(february.Id);
        storedFebruary!.TotalAmount.Should().Be(1000m);
        storedFebruary.LineItems.Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task RetroAdd_IsChargedOnTheNextInvoice()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));

        // C was effective 2/1 but enrolled after February was issued.
        Enroll("cov-C", "C", D(2026, 2, 1));
        var march = (await Generate(D(2026, 3, 1))).Invoice;

        march.Adjustments.Should().ContainSingle().Which.Should().Match<InvoiceAdjustment>(a =>
            a.Type == AdjustmentType.RetroAdd && a.CoverageId == "cov-C" && a.Amount == 500m);
        march.TotalAmount.Should().Be(1500m);

        // April does not bill February again.
        Clock.Now = D(2026, 3, 20);
        (await Generate(D(2026, 4, 1))).Invoice.Adjustments.Should().BeEmpty();
    }

    [SkippableFact]
    public async Task CompositeGroup_IsBilledPerTier_AndReconciledPerCoverage()
    {
        Options.Tenants[Tenant].Groups[Group] = new RatedBillingGroupOptions { BillFormat = BillFormat.Composite };
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        Enroll("cov-B", "B", D(2026, 1, 1));
        Enroll("cov-C", "C", D(2026, 1, 1));

        var february = (await Generate(D(2026, 2, 1))).Invoice;

        var tier = february.LineItems.Should().ContainSingle().Subject;
        tier.Should().Match<InvoiceLineItem>(l => l.Quantity == 3 && l.UnitRate == 500m && l.TotalPremium == 1500m && l.CoverageLevel == "EMP");
        tier.Components!.Select(c => c.CoverageId).Should().Equal("cov-A", "cov-B", "cov-C");
        february.BillFormat.Should().Be(BillFormat.Composite);
        february.MemberCount.Should().Be(3);

        Clock.Now = D(2026, 2, 25);
        (await Generate(D(2026, 3, 1))).Invoice.Adjustments.Should().BeEmpty("the composite line's components are what each coverage was billed");
    }

    // ── Coexistence with unrated invoices and voids ────────────────────

    [SkippableFact]
    public async Task PeriodWithAnUnratedInvoice_IsLeftAlone()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        var legacy = await Invoices.CreateAsync(new PremiumInvoice
        {
            InvoiceNumber = $"INV-{Group}-2026-03", GroupNumber = Group, BillingPeriodStart = D(2026, 3, 1),
            BillingPeriodEnd = D(2026, 3, 31), DueDate = D(2026, 3, 1),
            LineItems = { new InvoiceLineItem { MemberId = "A", CoverageId = "cov-A", TotalPremium = 480m } }
        });

        var outcome = await Generate(D(2026, 3, 1));

        outcome.Kind.Should().Be(RatedInvoiceOutcomeKind.UnratedInvoiceExists);
        outcome.Invoice.Id.Should().Be(legacy.Id);
        (await GroupInvoices()).Should().ContainSingle();
    }

    [SkippableFact]
    public async Task UnratedMonths_AreNotReconciled_ByTheFirstRatedInvoice()
    {
        // February was billed the original way (one line per member coverage, coverage-service premium).
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Invoices.CreateAsync(new PremiumInvoice
        {
            InvoiceNumber = $"INV-{Group}-2026-02", GroupNumber = Group, BillingPeriodStart = D(2026, 2, 1),
            BillingPeriodEnd = D(2026, 2, 28), DueDate = D(2026, 2, 1), Status = InvoiceStatus.Sent,
            LineItems = { new InvoiceLineItem { MemberId = "A", CoverageId = "cov-A", TotalPremium = 480m } }
        });

        var march = (await Generate(D(2026, 3, 1))).Invoice;

        march.Adjustments.Should().BeEmpty("unrated months are outside rated reconciliation (see the migration note)");
        march.TotalAmount.Should().Be(500m);
    }

    [SkippableFact]
    public async Task VoidedRatedInvoice_IsReissuedUnderANewId()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        var first = (await Generate(D(2026, 3, 1))).Invoice;
        await InvoiceWrites.UpdateWithRetryAsync(Invoices, first.Id, null, i => { i.Status = InvoiceStatus.Voided; return true; });

        var reissue = await Generate(D(2026, 3, 1));

        reissue.Kind.Should().Be(RatedInvoiceOutcomeKind.Created);
        reissue.Invoice.Id.Should().NotBe(first.Id).And.EndWith("-2");
        reissue.Invoice.InvoiceNumber.Should().Be(first.InvoiceNumber);
        (await GroupInvoices()).Should().HaveCount(2);
    }

    [SkippableFact]
    public async Task DuplicateId_IsRefusedByTheStore()
    {
        var invoice = new PremiumInvoice
        {
            Id = RatedInvoiceGenerator.InvoiceId(Tenant, Group, D(2026, 3, 1), 1), InvoiceNumber = "INV-X", GroupNumber = Group,
            BillingPeriodStart = D(2026, 3, 1), BillingPeriodEnd = D(2026, 3, 31), DueDate = D(2026, 3, 1)
        };
        await Invoices.CreateAsync(invoice);

        var again = () => Invoices.CreateAsync(new PremiumInvoice
        {
            Id = invoice.Id, InvoiceNumber = "INV-X", GroupNumber = Group,
            BillingPeriodStart = D(2026, 3, 1), BillingPeriodEnd = D(2026, 3, 31), DueDate = D(2026, 3, 1)
        });
        await again.Should().ThrowAsync<InvoiceAlreadyExistsException>();
    }

    [SkippableFact]
    public async Task OverdueQueries_SkipDrafts_AndClosedInvoices()
    {
        async Task<PremiumInvoice> Add(InvoiceStatus status, string number) => await Invoices.CreateAsync(new PremiumInvoice
        {
            InvoiceNumber = number, GroupNumber = Group, BillingPeriodStart = D(2026, 1, 1), BillingPeriodEnd = D(2026, 1, 31),
            DueDate = D(2026, 1, 1), Status = status, SubtotalPremium = 100m, TotalAmount = 100m, BalanceDue = 100m
        });
        await Add(InvoiceStatus.Draft, "INV-DRAFT");
        await Add(InvoiceStatus.Voided, "INV-VOID");
        await Add(InvoiceStatus.Sent, "INV-SENT");

        (await Invoices.GetOverdueAsync()).Select(i => i.InvoiceNumber).Should().Equal("INV-SENT");
        (await Invoices.GetByStatusAsync(InvoiceStatus.Draft)).Select(i => i.InvoiceNumber).Should().Equal("INV-DRAFT");
        (await Invoices.SearchAsync(status: InvoiceStatus.Voided)).Select(i => i.InvoiceNumber).Should().Equal("INV-VOID");
    }

    // ── Rate table store ───────────────────────────────────────────────

    [SkippableFact]
    public async Task RateTables_AreImmutableVersions()
    {
        var v1 = await SaveTierTable(500m);
        var v2 = await SaveTierTable(520m, expected: 1);

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);
        v2.ContentHash.Should().NotBe(v1.ContentHash);
        (await RateTables.GetVersionAsync(v1.RateTableId, 1))!.Table.TierRates!.EmployeeOnly.Should().Be(500m);
        (await RateTables.GetVersionAsync(v1.RateTableId, 1))!.ContentHash.Should().Be(v1.ContentHash);
        (await RateTableService().ListCurrentAsync()).Should().ContainSingle().Which.Version.Should().Be(2);

        var stale = () => SaveTierTable(530m, expected: 1);
        await stale.Should().ThrowAsync<RateTableVersionConflictException>();
        var duplicate = () => RateTables.CreateVersionAsync(new RateTableRecord { RateTableId = v1.RateTableId, Version = 2, Table = TierTable() });
        await duplicate.Should().ThrowAsync<RateTableVersionConflictException>();

        await RateTableService().WithdrawAsync(v1.RateTableId, new WithdrawRateTableRequest { Reason = "filed in error", ExpectedCurrentVersion = 2 }, "actuary-1");
        (await RateTableService().ListCurrentAsync()).Should().BeEmpty();
        (await RateTables.ListVersionsAsync(v1.RateTableId)).Select(v => v.Version).Should().Equal(1, 2, 3);
    }

    [SkippableFact]
    public async Task RateTables_ThatWouldOverlap_AreRejected()
    {
        await SaveTierTable();
        var overlapping = TierTable();
        overlapping.Id = "rt-other";
        overlapping.EffectiveFrom = D(2026, 6, 1);

        var save = () => RateTableService().SaveAsync(new SaveRateTableRequest { Table = overlapping }, "actuary-1");

        await save.Should().ThrowAsync<RateTableRejectedException>().WithMessage("*overlap*");
    }
}

/// <summary><see cref="RatedBillingScenarios"/> on a real mongod and the Mongo repositories.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class RatedBillingMongoTests(MongoRunnerFixture mongo) : RatedBillingScenarios
{
    private IMongoDatabase _database = null!;

    protected override Task<(IPremiumInvoiceRepository Invoices, IRateTableRepository RateTables)> CreateStoresAsync(IHttpContextAccessor http)
    {
        _database = mongo.CreateDatabase("pb_rated_billing");
        return Task.FromResult<(IPremiumInvoiceRepository, IRateTableRepository)>((
            new PremiumInvoiceRepositoryMongo(_database, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance),
            new RateTableRepositoryMongo(_database, http)));
    }

    public override Task DisposeAsync() => mongo.DropDatabaseAsync(_database);

    [Fact]
    public async Task RateTableVersions_OfTwoTenants_DoNotCollide()
    {
        var database = mongo.CreateDatabase("pb_rate_tenants");
        try
        {
            async Task<RateTableRecord> SaveFor(string tenant)
            {
                var http = new DefaultHttpContext();
                http.Items["TenantId"] = tenant;
                var repo = new RateTableRepositoryMongo(database, new HttpContextAccessor { HttpContext = http });
                return await new RateTableService(repo, new FixedClock(D(2026, 1, 1)))
                    .SaveAsync(new SaveRateTableRequest { Table = TierTable() }, "actuary");
            }

            var a = await SaveFor("tenant-a");
            var b = await SaveFor("tenant-b");

            a.Id.Should().NotBe(b.Id);
            a.Version.Should().Be(1);
            b.Version.Should().Be(1);
        }
        finally
        {
            await mongo.DropDatabaseAsync(database);
        }
    }
}
