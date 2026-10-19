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
/// repositories, on a standalone mongod (<see cref="RatedBillingMongoTests"/>,
/// renew-then-write fence) and a replica set
/// (<see cref="RatedBillingMongoReplicaSetTests"/>, transactional fence). coverage-service and member-service are in-memory fakes holding
/// synthetic members; every assertion re-reads what was saved.
/// </summary>
public abstract class RatedBillingScenarios : IAsyncLifetime
{
    protected const string Tenant = "tenant-rated";
    protected const string Group = "GRP-R1";

    protected IPremiumInvoiceRepository Invoices = null!;
    protected IRateTableRepository RateTables = null!;
    protected IBillingLeaseRepository Leases = null!;
    protected ISponsorAccountRepository Accounts = null!;
    protected readonly FakeCoverage Coverage = new();
    protected readonly FakeCensus Census = new();
    protected readonly FixedClock Clock = new(D(2026, 2, 20));
    protected RatedBillingOptions Options = null!;
    private RequestContext _http = null!;

    protected sealed record Stores(IPremiumInvoiceRepository Invoices, IRateTableRepository RateTables,
        IBillingLeaseRepository Leases, ISponsorAccountRepository Accounts);

    protected abstract Task<Stores> CreateStoresAsync(IHttpContextAccessor http);

    public abstract Task DisposeAsync();

    public async Task InitializeAsync()
    {
        _http = new RequestContext { HttpContext = new DefaultHttpContext() };
        _http.HttpContext.Items["TenantId"] = Tenant;
        var stores = await CreateStoresAsync(_http);
        (Invoices, RateTables, Leases, Accounts) = (stores.Invoices, stores.RateTables, stores.Leases, stores.Accounts);
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

    protected RatedInvoiceGenerator Generator(ICoverageServiceClient? coverage = null) => new(Invoices, RateTables, coverage ?? Coverage, Census,
        Leases, Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<RatedInvoiceGenerator>.Instance, Accounts);

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

    [Fact]
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

    [Fact]
    public async Task ConcurrentGenerations_CreateOneInvoice()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Generate(D(2026, 3, 1)))));

        outcomes.Count(o => o.Kind == RatedInvoiceOutcomeKind.Created).Should().Be(1);
        outcomes.Should().OnlyContain(o => o.Kind == RatedInvoiceOutcomeKind.Created || o.Kind == RatedInvoiceOutcomeKind.AlreadyIssued);
        (await GroupInvoices()).Should().ContainSingle();
    }

    [Fact]
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
        var issue = () => Generator().IssueDraftAsync(draft.Invoice.Id, Tenant, "billing-user");
        await issue.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rating exception*");

        // The actuary extends the table (version 2); regenerating the draft rates March and issues it.
        var v2 = await SaveTierTable(to: PlanYearEnd, expected: february.Version);
        var regenerated = await Generator().RegenerateDraftAsync(draft.Invoice.Id, Tenant, "billing-user");

        regenerated.Kind.Should().Be(RatedInvoiceOutcomeKind.Regenerated);
        var stored = (await GroupInvoices()).Should().ContainSingle().Subject;
        stored.Id.Should().Be(draft.Invoice.Id);
        stored.Status.Should().Be(InvoiceStatus.Generated);
        stored.RatingExceptions.Should().BeEmpty();
        stored.RatingRevision.Should().Be(3, "computed by the first run, by the refused issue (which recomputes), and by the regeneration");
        stored.LineItems.Single().Should().Match<InvoiceLineItem>(l => l.TotalPremium == 500m && l.RateTableVersion == v2.Version);

        var again = () => Generator().RegenerateDraftAsync(draft.Invoice.Id, Tenant, "billing-user");
        await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*never changed*");
    }

    [Fact]
    public async Task HeldForReview_StaysDraftUntilIssued()
    {
        Options.Tenants[Tenant].HoldForReview = true;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));

        var draft = await Generate(D(2026, 3, 1));
        draft.Invoice.Status.Should().Be(InvoiceStatus.Draft);
        draft.Invoice.RatingExceptions.Should().BeEmpty();

        var issued = await Generator().IssueDraftAsync(draft.Invoice.Id, Tenant, "billing-user");

        issued.Status.Should().Be(InvoiceStatus.Generated);
        issued.IssuedAt.Should().Be(Clock.Now);
        (await Invoices.GetByIdAsync(draft.Invoice.Id))!.Status.Should().Be(InvoiceStatus.Generated);
        (await Generate(D(2026, 3, 1))).Kind.Should().Be(RatedInvoiceOutcomeKind.AlreadyIssued);
    }

    // ── Retro changes land on the next invoice ─────────────────────────

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task RateTables_ThatWouldOverlap_AreRejected()
    {
        await SaveTierTable();
        var overlapping = TierTable();
        overlapping.Id = "rt-other";
        overlapping.EffectiveFrom = D(2026, 6, 1);

        var save = () => RateTableService().SaveAsync(new SaveRateTableRequest { Table = overlapping }, "actuary-1");

        await save.Should().ThrowAsync<RateTableRejectedException>().WithMessage("*overlap*");
    }

    // ── Review regressions: one retro change is billed once ────────────

    private async Task<decimal> RetroFor(string coverageId, DateTime month) =>
        (await GroupInvoices()).Where(i => i.Status != InvoiceStatus.Voided).SelectMany(i => i.Adjustments)
            .Where(a => a.CoverageId == coverageId && a.ServicePeriodStart == month).Sum(a => a.Amount);

    [Fact]
    public async Task HeldDraft_BlocksLaterPeriods_SoARetroChangeIsBilledOnce()
    {
        // Review probe: Feb issued; B effective 2/1 arrives; March is held as Draft (+500 for Feb).
        // April must not reconcile February again while March is unissued.
        Options.Tenants[Tenant].HoldForReview = true;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        var february = await Generate(D(2026, 2, 1));
        await Generator().IssueDraftAsync(february.Invoice.Id, Tenant, "u");
        Enroll("cov-B", "B", D(2026, 2, 1));

        var march = await Generate(D(2026, 3, 1));
        var april = await Generate(D(2026, 4, 1));

        april.Kind.Should().Be(RatedInvoiceOutcomeKind.BlockedByEarlierDraft);
        april.Invoice.Id.Should().Be(march.Invoice.Id);
        (await GroupInvoices()).Should().NotContain(i => i.BillingPeriodStart == D(2026, 4, 1));

        await Generator().IssueDraftAsync(march.Invoice.Id, Tenant, "u");
        var aprilDraft = await Generate(D(2026, 4, 1));
        await Generator().IssueDraftAsync(aprilDraft.Invoice.Id, Tenant, "u");

        (await RetroFor("cov-B", D(2026, 2, 1))).Should().Be(500m, "B's February is charged once");
    }

    [Fact]
    public async Task IssuingAnOutOfDateDraft_RegeneratesIt_AndRefuses()
    {
        Options.Tenants[Tenant].HoldForReview = true;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        var draft = (await Generate(D(2026, 3, 1))).Invoice;
        draft.TotalAmount.Should().Be(500m);

        // Coverage changes after the reviewer looked at 500.00.
        Enroll("cov-B", "B", D(2026, 3, 1));
        var issue = () => Generator().IssueDraftAsync(draft.Id, Tenant, "u");

        await issue.Should().ThrowAsync<DraftOutOfDateException>();
        var regenerated = await Invoices.GetByIdAsync(draft.Id);
        regenerated!.Status.Should().Be(InvoiceStatus.Draft);
        regenerated.TotalAmount.Should().Be(1000m);

        // Reviewed again: now it issues exactly what was reviewed.
        (await Generator().IssueDraftAsync(draft.Id, Tenant, "u")).Should().Match<PremiumInvoice>(i =>
            i.Status == InvoiceStatus.Generated && i.TotalAmount == 1000m);
    }

    [Fact]
    public async Task DraftIssuedAfterTheBasisChanged_IsRefused()
    {
        // An April Draft computed before March was issued (out-of-order periods) reconciled without March.
        Options.Tenants[Tenant].HoldForReview = true;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        var april = (await Generate(D(2026, 4, 1))).Invoice;
        var march = (await Generate(D(2026, 3, 1))).Invoice;
        await Generator().IssueDraftAsync(march.Id, Tenant, "u");

        var issue = () => Generator().IssueDraftAsync(april.Id, Tenant, "u");

        await issue.Should().ThrowAsync<DraftOutOfDateException>();
        (await Invoices.GetByIdAsync(april.Id))!.ReconciledInvoiceIds.Should().Contain(march.Id);
    }

    private sealed class BarrierCoverage(FakeCoverage inner, int parties) : ICoverageServiceClient
    {
        private readonly Barrier _barrier = new(parties);
        public int TimedOut;

        public Task<List<CoverageDto>> GetActiveCoveragesByGroupAsync(string t, string g, CancellationToken c = default) =>
            throw new NotSupportedException();

        public async Task<List<CoverageDto>> GetAllCoveragesByGroupAsync(string t, string g, CancellationToken c = default)
        {
            var records = await inner.GetAllCoveragesByGroupAsync(t, g, c);
            // Both generations try to be here at once; under the group lease only one can be.
            if (!await Task.Run(() => _barrier.SignalAndWait(TimeSpan.FromSeconds(2)), c))
                Interlocked.Increment(ref TimedOut);
            return records;
        }
    }

    [Fact]
    public async Task ConcurrentPeriodsOfOneGroup_AreSerialized_SoARetroChangeIsBilledOnce()
    {
        // Review probe: March and April generated at the same moment, both after B's retro add for February.
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));
        Enroll("cov-B", "B", D(2026, 2, 1));
        var barrier = new BarrierCoverage(Coverage, 2);

        await Task.WhenAll(
            Task.Run(() => Generator(barrier).GenerateAsync(Sponsor, Tenant, D(2026, 3, 1), "r", "u")),
            Task.Run(() => Generator(barrier).GenerateAsync(Sponsor, Tenant, D(2026, 4, 1), "r", "u")));

        barrier.TimedOut.Should().BeGreaterThan(0, "the second generation waited for the first one's lease");
        (await RetroFor("cov-B", D(2026, 2, 1))).Should().Be(500m);
    }

    [Fact]
    public async Task LeaseDocuments_AreNeverReadOrWrittenAsInvoices()
    {
        // Leases share the invoices' container/collection; their id is computable from tenant and group.
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));
        var held = await Leases.TryAcquireAsync(Group, "holder", Clock.Now, TimeSpan.FromMinutes(5));
        var leaseId = BillingLease.IdFor(Tenant, Group);

        (await Invoices.GetByIdAsync(leaseId)).Should().BeNull();
        (await Invoices.SearchAsync()).Should().OnlyContain(i => i.GroupNumber == Group && i.InvoiceNumber.StartsWith("INV-"));
        (await Invoices.GetByGroupNumberAsync(Group)).Should().ContainSingle();
        (await Invoices.GetOverdueAsync()).Should().NotContain(i => i.Id == leaseId);

        var overwrite = () => Invoices.UpdateAsync(new PremiumInvoice { Id = leaseId, TenantId = Tenant, InvoiceNumber = "X", GroupNumber = Group });
        await overwrite.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not an invoice*");
        var create = () => Invoices.CreateAsync(new PremiumInvoice { Id = leaseId, InvoiceNumber = "X", GroupNumber = Group });
        await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not an invoice*");
        var delete = () => Invoices.DeleteAsync(leaseId);
        await delete.Should().ThrowAsync<InvalidOperationException>();

        // The lease itself is untouched and still renewable by its holder.
        (await Leases.RenewAsync(held!, Clock.Now, TimeSpan.FromMinutes(5))).Should().BeTrue();
    }

    [Fact]
    public async Task GroupLease_IsExclusiveUntilReleasedOrExpired()
    {
        var now = D(2026, 3, 1);
        var one = await Leases.TryAcquireAsync(Group, "one", now, TimeSpan.FromMinutes(5));
        one.Should().NotBeNull();
        (await Leases.TryAcquireAsync(Group, "two", now.AddMinutes(1), TimeSpan.FromMinutes(5))).Should().BeNull();
        (await Leases.TryAcquireAsync("OTHER-GROUP", "two", now, TimeSpan.FromMinutes(5))).Should().NotBeNull();

        await Leases.ReleaseAsync(new BillingLeaseHandle { GroupNumber = Group, Owner = "two" }); // not the holder: no effect
        (await Leases.TryAcquireAsync(Group, "three", now.AddMinutes(2), TimeSpan.FromMinutes(5))).Should().BeNull();
        (await Leases.RenewAsync(one!, now.AddMinutes(3), TimeSpan.FromMinutes(5))).Should().BeTrue("the holder extends its lease");

        // Expired (renewed to 3 + 5 = 8 minutes): "two" takes it over, and "one" can no longer renew it.
        var two = await Leases.TryAcquireAsync(Group, "two", now.AddMinutes(9), TimeSpan.FromMinutes(5));
        two.Should().NotBeNull("the first lease expired");
        (await Leases.RenewAsync(one!, now.AddMinutes(9), TimeSpan.FromMinutes(5))).Should().BeFalse("its lease was taken over");
        await Leases.ReleaseAsync(one!); // no longer the holder: no effect
        (await Leases.TryAcquireAsync(Group, "three", now.AddMinutes(10), TimeSpan.FromMinutes(5))).Should().BeNull();

        await Leases.ReleaseAsync(two!);
        (await Leases.TryAcquireAsync(Group, "three", now.AddMinutes(10), TimeSpan.FromMinutes(5))).Should().NotBeNull();
    }

    [Fact]
    public async Task BusyGroup_FailsAfterTheWait_InsteadOfComputingFromAStaleLedger()
    {
        Options.GroupLeaseWaitSeconds = 0;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        (await Leases.TryAcquireAsync(Group, "someone-else", Clock.Now, TimeSpan.FromMinutes(5))).Should().NotBeNull();

        var generate = () => Generate(D(2026, 3, 1));

        await generate.Should().ThrowAsync<GroupBillingBusyException>();
        (await GroupInvoices()).Should().BeEmpty();
    }

    [Fact]
    public async Task FencedWrite_ByAHolderWhoseLeaseWasTakenOver_WritesNothing()
    {
        var now = D(2026, 3, 1);
        var stale = await Leases.TryAcquireAsync(Group, "stale", now, TimeSpan.FromMinutes(5));
        (await Leases.TryAcquireAsync(Group, "current", now.AddMinutes(6), TimeSpan.FromMinutes(5))).Should().NotBeNull();
        var invoice = new PremiumInvoice
        {
            Id = "fenced-1", InvoiceNumber = "INV-FENCED", GroupNumber = Group,
            BillingPeriodStart = D(2026, 3, 1), BillingPeriodEnd = D(2026, 3, 31), DueDate = D(2026, 3, 1)
        };

        var write = () => Invoices.CreateFencedAsync(invoice, Leases, stale!, now.AddMinutes(7), TimeSpan.FromMinutes(5));

        await write.Should().ThrowAsync<BillingLeaseLostException>();
        (await Invoices.GetByIdAsync("fenced-1")).Should().BeNull();
    }

    private sealed class StallingCoverage(FakeCoverage inner, FixedClock clock) : ICoverageServiceClient
    {
        public readonly TaskCompletionSource FirstInside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task<List<CoverageDto>> GetActiveCoveragesByGroupAsync(string t, string g, CancellationToken c = default) =>
            throw new NotSupportedException();

        public async Task<List<CoverageDto>> GetAllCoveragesByGroupAsync(string t, string g, CancellationToken c = default)
        {
            var records = await inner.GetAllCoveragesByGroupAsync(t, g, c);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                clock.Now = clock.Now.AddSeconds(400); // the first run stalls past its 300 s lease
                FirstInside.SetResult();
                await SecondDone.Task;
            }
            return records;
        }
    }

    [Fact]
    public async Task ExpiredLease_StaleRunWritesNothing_SoARetroChangeIsBilledOnce()
    {
        // Review probe 2: March stalls in coverage-service past its lease; April takes the lease
        // over, reconciles February and writes; March then resumes. It must not write.
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));
        Enroll("cov-B", "B", D(2026, 2, 1));
        var coverage = new StallingCoverage(Coverage, Clock);

        var march = Task.Run(() => Generator(coverage).GenerateAsync(Sponsor, Tenant, D(2026, 3, 1), "r", "u"));
        await coverage.FirstInside.Task;
        try
        {
            (await Generator(coverage).GenerateAsync(Sponsor, Tenant, D(2026, 4, 1), "r", "u")).Kind.Should().Be(RatedInvoiceOutcomeKind.Created);
        }
        finally
        {
            coverage.SecondDone.SetResult();
        }

        await FluentActions.Awaiting(() => march).Should().ThrowAsync<BillingLeaseLostException>();
        (await GroupInvoices()).Should().NotContain(i => i.BillingPeriodStart == D(2026, 3, 1));
        (await RetroFor("cov-B", D(2026, 2, 1))).Should().Be(500m);
    }

    private sealed class VoidRacingInvoices(IPremiumInvoiceRepository inner) : IPremiumInvoiceRepository
    {
        public bool Armed;
        public Task<PremiumInvoice?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<IEnumerable<PremiumInvoice>> GetByBillingPeriodAsync(DateTime d) => inner.GetByBillingPeriodAsync(d);
        public Task<IEnumerable<PremiumInvoice>> SearchAsync(string? g = null, DateTime? f = null, DateTime? t = null, InvoiceStatus? s = null, int page = 1, int pageSize = 50) => inner.SearchAsync(g, f, t, s, page, pageSize);
        public Task<IEnumerable<PremiumInvoice>> GetByInvoiceNumberAsync(string n) => inner.GetByInvoiceNumberAsync(n);
        public Task<IEnumerable<PremiumInvoice>> GetByGroupNumberAsync(string g) => inner.GetByGroupNumberAsync(g);
        public Task<IEnumerable<PremiumInvoice>> GetByStatusAsync(InvoiceStatus s) => inner.GetByStatusAsync(s);
        public Task<IEnumerable<PremiumInvoice>> GetOverdueAsync() => inner.GetOverdueAsync();
        public Task<IEnumerable<PremiumInvoice>> ListByMemberAsync(string m, int take = 12) => inner.ListByMemberAsync(m, take);
        public Task<PremiumInvoice> CreateAsync(PremiumInvoice i) => inner.CreateAsync(i);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);

        public async Task<PremiumInvoice> UpdateAsync(PremiumInvoice invoice)
        {
            if (Armed && invoice.Status != InvoiceStatus.Draft)
            {
                Armed = false;
                // A writer that takes no lease voids the Draft between the computation and this save.
                var current = await inner.GetByIdAsync(invoice.Id);
                current!.Status = InvoiceStatus.Voided;
                await inner.UpdateAsync(current);
                throw new ConcurrencyConflictException("raced");
            }
            return await inner.UpdateAsync(invoice);
        }
    }

    private async Task<(decimal AccountCredit, decimal InvoiceCredit)> MarchCredits()
    {
        var account = await Accounts.GetAsync(Group);
        var invoices = (await GroupInvoices()).Where(i => i.BillingPeriodStart == D(2026, 3, 1) && i.Status != InvoiceStatus.Voided);
        return (account?.UnappliedCredit ?? 0m, invoices.Sum(i => -i.TotalAmount));
    }

    private async Task<PremiumInvoice> HeldNetCreditDraft()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        Enroll("cov-B", "B", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));
        foreach (var record in Coverage.Records)
            record.TerminationDate = D(2026, 1, 31);
        Options.Tenants[Tenant].HoldForReview = true;
        var draft = (await Generate(D(2026, 3, 1))).Invoice;
        draft.Status.Should().Be(InvoiceStatus.Draft);
        Options.Tenants[Tenant].HoldForReview = false;
        return draft;
    }

    [Fact]
    public async Task NetCredit_DraftVoidedDuringIssue_CreditIsMovedOnce()
    {
        // Review probe 2: the save of the issued credit invoice fails because the Draft was voided
        // meanwhile; the retry issues reissue -2. The account must hold exactly one credit.
        var draft = await HeldNetCreditDraft();
        var racing = new VoidRacingInvoices(Invoices) { Armed = true };
        var generator = new RatedInvoiceGenerator(racing, RateTables, Coverage, Census, Leases,
            Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<RatedInvoiceGenerator>.Instance, Accounts);

        try { await generator.RegenerateDraftAsync(draft.Id, Tenant, "u"); } catch (Exception) { }
        try { await Generate(D(2026, 3, 1)); } catch (Exception) { }

        var (account, invoices) = await MarchCredits();
        invoices.Should().Be(1000m);
        account.Should().Be(invoices, "the credit on the account matches the non-void credit invoice");
    }

    private sealed class CrashOnceAccounts(ISponsorAccountRepository inner) : ISponsorAccountRepository
    {
        public bool Armed = true;
        public Task<SponsorAccount?> GetAsync(string groupNumber) => inner.GetAsync(groupNumber);

        public Task<SponsorAccount> UpdateAsync(string groupNumber, Action<SponsorAccount> change)
        {
            if (Armed)
            {
                Armed = false;
                throw new TimeoutException("the process died before the account was credited");
            }
            return inner.UpdateAsync(groupNumber, change);
        }
    }

    [Fact]
    public async Task NetCredit_CrashAfterTheSave_IsPostedByTheNextRun_Once()
    {
        var draft = await HeldNetCreditDraft();
        var crashing = new RatedInvoiceGenerator(Invoices, RateTables, Coverage, Census, Leases,
            Microsoft.Extensions.Options.Options.Create(Options), Clock, NullLogger<RatedInvoiceGenerator>.Instance,
            new CrashOnceAccounts(Accounts));

        var issue = () => crashing.RegenerateDraftAsync(draft.Id, Tenant, "u");
        await issue.Should().ThrowAsync<TimeoutException>();
        (await Invoices.GetByIdAsync(draft.Id))!.Status.Should().Be(InvoiceStatus.Paid, "the invoice was saved before the account post");
        (await MarchCredits()).AccountCredit.Should().Be(0m);

        // The next run finds it issued and posts the missing credit, once.
        (await Generate(D(2026, 3, 1))).Kind.Should().Be(RatedInvoiceOutcomeKind.AlreadyIssued);
        (await Generate(D(2026, 3, 1))).Kind.Should().Be(RatedInvoiceOutcomeKind.AlreadyIssued);
        (await MarchCredits()).Should().Be((1000m, 1000m));
    }

    [Fact]
    public async Task VoidOfARatedInvoice_TakesTheGroupLease()
    {
        Options.Tenants[Tenant].HoldForReview = true;
        Options.GroupLeaseWaitSeconds = 0;
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        var draft = (await Generate(D(2026, 3, 1))).Invoice;
        var actor = new Mock<CloudHealthOffice.Infrastructure.Security.ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns("finance-user");
        var billing = new PremiumBillingService.Services.PremiumBillingService(
            new Mock<IBillingRunRepository>().Object, Invoices, new Mock<ISponsorServiceClient>().Object, Coverage, actor.Object,
            NullLogger<PremiumBillingService.Services.PremiumBillingService>.Instance, Accounts, Generator(),
            Microsoft.Extensions.Options.Options.Create(Options));

        var held = await Leases.TryAcquireAsync(Group, "a-generation", Clock.Now, TimeSpan.FromMinutes(5));
        var voidWhileHeld = () => billing.VoidInvoiceAsync(draft.Id, "duplicate");
        await voidWhileHeld.Should().ThrowAsync<GroupBillingBusyException>();
        (await Invoices.GetByIdAsync(draft.Id))!.Status.Should().Be(InvoiceStatus.Draft);

        await Leases.ReleaseAsync(held!);
        (await billing.VoidInvoiceAsync(draft.Id, "duplicate")).Status.Should().Be(InvoiceStatus.Voided);
    }

    // ── Review regressions: credits and corrections ────────────────────

    [Fact]
    public async Task NetCredit_MovesToTheSponsorsUnappliedCredit_Once()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        Enroll("cov-B", "B", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));

        // Both terminated 1/31, reported after February was billed: March owes nothing and credits 1,000.
        foreach (var record in Coverage.Records)
            record.TerminationDate = D(2026, 1, 31);
        var march = (await Generate(D(2026, 3, 1))).Invoice;

        march.TotalAmount.Should().Be(-1000m);
        march.Status.Should().Be(InvoiceStatus.Paid);
        march.BalanceDue.Should().Be(0m);
        march.Payments.Should().ContainSingle().Which.Should().Match<InvoicePayment>(p => p.Amount == -1000m && p.PaymentMethod == "SponsorAccountCredit");
        var account = await Accounts.GetAsync(Group);
        account!.UnappliedCredit.Should().Be(1000m);
        account.Entries.Should().ContainSingle(e => e.Type == SponsorAccountEntryType.RatedInvoiceCredit && e.InvoiceId == march.Id);

        (await Generate(D(2026, 3, 1))).Kind.Should().Be(RatedInvoiceOutcomeKind.AlreadyIssued);
        (await Accounts.GetAsync(Group))!.UnappliedCredit.Should().Be(1000m);
    }

    [Fact]
    public async Task CoverageThatVanished_IsAnException_NotACredit()
    {
        await SaveTierTable();
        Enroll("cov-A", "A", D(2026, 1, 1));
        Enroll("cov-B", "B", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));

        Coverage.Records.RemoveAll(r => r.CoverageId == "cov-B");
        var march = (await Generate(D(2026, 3, 1))).Invoice;

        march.Status.Should().Be(InvoiceStatus.Draft);
        march.Adjustments.Should().BeEmpty("a missing record is not a termination");
        march.RatingExceptions.Should().ContainSingle().Which.Should().Match<InvoiceRatingException>(e =>
            e.Code == RatedInvoiceGenerator.CoverageMissing && e.CoverageId == "cov-B");
    }

    [Fact]
    public async Task RateCorrection_OfInvoicedMonths_IsHeldForReview()
    {
        var v1 = await SaveTierTable(500m);
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));

        // The table is corrected (same dates, EE 520): February re-prices under v2.
        await SaveTierTable(520m, expected: v1.Version);
        var march = (await Generate(D(2026, 3, 1))).Invoice;

        march.Status.Should().Be(InvoiceStatus.Draft);
        march.RatingExceptions.Should().BeEmpty();
        march.ReviewReasons.Should().ContainSingle(r => r.StartsWith("RATE_CORRECTION"));
        march.Adjustments.Should().ContainSingle().Which.Should().Match<InvoiceAdjustment>(a =>
            a.IsRateCorrection && a.Type == AdjustmentType.RateChange && a.Amount == 20m);

        var issued = await Generator().IssueDraftAsync(march.Id, Tenant, "reviewer");
        issued.Should().Match<PremiumInvoice>(i => i.Status == InvoiceStatus.Generated && i.TotalAmount == 540m);
    }

    [Fact]
    public async Task RateCorrection_BelowTheThreshold_IsIssued()
    {
        Options.Tenants[Tenant].RateCorrectionReviewThreshold = 25m;
        var v1 = await SaveTierTable(500m);
        Enroll("cov-A", "A", D(2026, 1, 1));
        await Generate(D(2026, 2, 1));
        await SaveTierTable(520m, expected: v1.Version);

        (await Generate(D(2026, 3, 1))).Invoice.Status.Should().Be(InvoiceStatus.Generated);
    }

    [Fact]
    public async Task MemberPremiumTab_TakesTheLastIssuedInvoices_NotDrafts()
    {
        for (var month = 1; month <= 3; month++)
            await Invoices.CreateAsync(new PremiumInvoice
            {
                InvoiceNumber = $"INV-{month}", GroupNumber = Group, BillingPeriodStart = D(2026, month, 1),
                BillingPeriodEnd = D(2026, month, 28), DueDate = D(2026, month, 1),
                Status = month == 3 ? InvoiceStatus.Draft : InvoiceStatus.Sent,
                LineItems = { new InvoiceLineItem { MemberId = "MEM-1", TotalPremium = 100m } }
            });

        (await Invoices.ListByMemberAsync("MEM-1", take: 2)).Select(i => i.InvoiceNumber).Should().Equal("INV-2", "INV-1");
    }
}

/// <summary><see cref="RatedBillingScenarios"/> on a real mongod and the Mongo repositories.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class RatedBillingMongoTests(MongoRunnerFixture mongo) : RatedBillingScenarios
{
    private IMongoDatabase _database = null!;

    protected override Task<Stores> CreateStoresAsync(IHttpContextAccessor http)
    {
        _database = mongo.CreateDatabase("pb_rated_billing");
        return Task.FromResult(new Stores(
            new PremiumInvoiceRepositoryMongo(_database, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance),
            new RateTableRepositoryMongo(_database, http),
            new BillingLeaseRepositoryMongo(_database, http),
            new SponsorAccountRepositoryMongo(_database, http)));
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

/// <summary>A mongod started as a single-node replica set, so multi-document transactions work.</summary>
public sealed class MongoReplicaSetFixture : IDisposable
{
    private readonly EphemeralMongo.IMongoRunner _runner;

    public MongoReplicaSetFixture()
    {
        var options = TestMongo.Options();
        options.UseSingleNodeReplicaSet = true;
        _runner = EphemeralMongo.MongoRunner.Run(options);
        Client = new MongoClient(_runner.ConnectionString);
    }

    public IMongoClient Client { get; }

    public void Dispose() => _runner.Dispose();
}

/// <summary>
/// <see cref="RatedBillingScenarios"/> on a replica-set mongod: invoice writes are fenced
/// by the group lease inside a multi-document transaction (the standalone run above uses
/// renew-then-write).
/// </summary>
public sealed class RatedBillingMongoReplicaSetTests(MongoReplicaSetFixture mongo) : RatedBillingScenarios, IClassFixture<MongoReplicaSetFixture>
{
    private IMongoDatabase _database = null!;

    protected override Task<Stores> CreateStoresAsync(IHttpContextAccessor http)
    {
        _database = mongo.Client.GetDatabase($"pb_rated_rs_{Guid.NewGuid():N}");
        return Task.FromResult(new Stores(
            new PremiumInvoiceRepositoryMongo(_database, http, NullLogger<PremiumInvoiceRepositoryMongo>.Instance),
            new RateTableRepositoryMongo(_database, http),
            new BillingLeaseRepositoryMongo(_database, http),
            new SponsorAccountRepositoryMongo(_database, http)));
    }

    public override Task DisposeAsync() => mongo.Client.DropDatabaseAsync(_database.DatabaseNamespace.DatabaseName);

    [Fact]
    public async Task LeasesLiveInTheInvoiceCollection_SoTheFencedWriteIsASingleCollectionTransaction()
    {
        // Azure Cosmos DB for MongoDB (RU) supports multi-document transactions only within one collection.
        (await Leases.TryAcquireAsync(Group, "holder", Clock.Now, TimeSpan.FromMinutes(5))).Should().NotBeNull();

        var raw = _database.GetCollection<MongoDB.Bson.BsonDocument>("PremiumInvoices");
        (await raw.CountDocumentsAsync(new MongoDB.Bson.BsonDocument("DocType", BillingLease.DocumentType))).Should().Be(1);
        _database.ListCollectionNames().ToList().Should().NotContain("BillingLeases");
    }
}
