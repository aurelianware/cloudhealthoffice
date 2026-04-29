using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Repositories;
using EphemeralMongo;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace CloudHealthOffice.ClaimsService.Tests.Repositories;

/// <summary>
/// Mongo-backed coverage for the claim version chain (5.1):
/// <list type="bullet">
///   <item>Hydration of legacy rows (ClaimVersionId, VersionState, VersionNumber).</item>
///   <item>CreateAsync seeds the chain on first write.</item>
///   <item>UpdateAsync rejects writes against terminal-state versions.</item>
///   <item>GetLatestVersionAsync, GetVersionAsync, ListVersionsAsync return
///         versioned rows by chain key.</item>
///   <item>UpdateAdjudicationProjectionAsync patches without rolling a new
///         version (the projection-metadata bypass).</item>
/// </list>
/// </summary>
public class ClaimRepositoryVersioningTests : IAsyncLifetime
{
    private const string Tenant = "tenant-claims";

    private IMongoRunner _runner = null!;
    private IMongoDatabase _database = null!;
    private ClaimRepositoryMongo _repo = null!;
    private DefaultHttpContext _ctx = null!;

    public Task InitializeAsync()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions { ConnectionTimeout = TimeSpan.FromSeconds(30) });
        var client = new MongoClient(_runner.ConnectionString);
        _database = client.GetDatabase($"claim_repo_test_{Guid.NewGuid():N}");
        _ctx = new DefaultHttpContext();
        _ctx.Items["TenantId"] = Tenant;
        var accessor = new HttpContextAccessor { HttpContext = _ctx };
        _repo = new ClaimRepositoryMongo(_database, accessor, NullLogger<ClaimRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _runner.Dispose(); }
        catch (TypeLoadException) { /* see ProviderVersionEventPublisherTests note */ }
        return Task.CompletedTask;
    }

    private static Claim Sample(string id = "", string claimNumber = "CN-001") => new()
    {
        Id = id,
        TenantId = Tenant,
        ClaimNumber = claimNumber,
        MemberId = "M1",
        BillingProviderNPI = "1234567890",
        ClaimType = ClaimType.Professional,
        Status = ClaimStatus.Submitted,
        TotalChargeAmount = 100m,
        ServiceDateFrom = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
        SubmittedDate = new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task LegacyRow_without_version_fields_hydrates_on_read()
    {
        // Insert a row that predates the version-chain feature: no
        // ClaimVersionId / VersionState / VersionNumber set. Status=Approved
        // should map to VersionState=Adjudicated on hydration.
        var collection = _database.GetCollection<Claim>("Claims");
        var doc = Sample("legacy-1");
        doc.Status = ClaimStatus.Approved;
        await collection.InsertOneAsync(doc);

        var hydrated = await _repo.GetByIdAsync("legacy-1");

        hydrated.Should().NotBeNull();
        hydrated!.ClaimVersionId.Should().Be("legacy-1");
        hydrated.VersionNumber.Should().Be(1);
        hydrated.VersionState.Should().Be(ClaimVersionState.Adjudicated);
    }

    [Theory]
    [InlineData(ClaimStatus.Submitted, ClaimVersionState.Submitted)]
    [InlineData(ClaimStatus.Received, ClaimVersionState.Submitted)]
    [InlineData(ClaimStatus.InAdjudication, ClaimVersionState.Submitted)]
    [InlineData(ClaimStatus.Pended, ClaimVersionState.Submitted)]
    [InlineData(ClaimStatus.Approved, ClaimVersionState.Adjudicated)]
    [InlineData(ClaimStatus.PartiallyPaid, ClaimVersionState.Paid)]
    [InlineData(ClaimStatus.Paid, ClaimVersionState.Paid)]
    [InlineData(ClaimStatus.Denied, ClaimVersionState.Denied)]
    [InlineData(ClaimStatus.Voided, ClaimVersionState.Voided)]
    public void Status_to_VersionState_mapping_is_pinned(ClaimStatus legacy, ClaimVersionState expected)
    {
        // The hydration map matters for legacy doc deserialization; pin it
        // here so the table is reviewed if anyone changes the mapping.
        ClaimRepository.MapStatusToVersionState(legacy).Should().Be(expected);
    }

    [Fact]
    public async Task CreateAsync_seeds_chain_on_first_write()
    {
        var fresh = Sample("");
        fresh.ClaimNumber = "CN-CREATE";
        var written = await _repo.CreateAsync(fresh);

        written.Id.Should().NotBeNullOrWhiteSpace();
        written.ClaimVersionId.Should().Be(written.Id);
        written.VersionNumber.Should().Be(1);
        written.VersionState.Should().Be(ClaimVersionState.Submitted);
    }

    [Fact]
    public async Task CreateAsync_preserves_caller_supplied_chain_metadata()
    {
        // When the adjustment workflow (5.12) creates an N+1 version it
        // supplies ClaimVersionId, VersionNumber, and PredecessorVersionId
        // explicitly. CreateAsync must not overwrite those.
        var caller = Sample("explicit-row-id");
        caller.ClaimVersionId = "shared-chain";
        caller.VersionNumber = 2;
        caller.VersionState = ClaimVersionState.Submitted;
        caller.PredecessorVersionId = "prior-row-id";

        var written = await _repo.CreateAsync(caller);

        written.Id.Should().Be("explicit-row-id");
        written.ClaimVersionId.Should().Be("shared-chain");
        written.VersionNumber.Should().Be(2);
        written.PredecessorVersionId.Should().Be("prior-row-id");
    }

    [Fact]
    public async Task UpdateAsync_against_Paid_terminal_throws_state_exception()
    {
        var paid = Sample("row-paid");
        paid.ClaimVersionId = "chain-paid";
        paid.VersionNumber = 1;
        paid.VersionState = ClaimVersionState.Paid;
        paid.Status = ClaimStatus.Paid;
        await _repo.CreateAsync(paid);

        paid.PaidDate = DateTime.UtcNow.AddDays(-1);
        var act = async () => await _repo.UpdateAsync(paid);

        var ex = await act.Should().ThrowAsync<ClaimVersionStateException>();
        ex.Which.CurrentState.Should().Be(ClaimVersionState.Paid);
        ex.Which.ClaimVersionId.Should().Be("chain-paid");
    }

    [Theory]
    [InlineData(ClaimVersionState.Denied)]
    [InlineData(ClaimVersionState.Voided)]
    [InlineData(ClaimVersionState.Adjusted)]
    public async Task UpdateAsync_against_each_terminal_state_throws(ClaimVersionState state)
    {
        var doc = Sample($"row-{state}");
        doc.ClaimVersionId = $"chain-{state}";
        doc.VersionNumber = 1;
        doc.VersionState = state;
        await _repo.CreateAsync(doc);

        var act = async () => await _repo.UpdateAsync(doc);
        await act.Should().ThrowAsync<ClaimVersionStateException>();
    }

    [Fact]
    public async Task UpdateAsync_against_non_terminal_state_succeeds()
    {
        var doc = await _repo.CreateAsync(Sample("row-active"));
        doc.TotalChargeAmount = 250m;

        var updated = await _repo.UpdateAsync(doc);

        updated.TotalChargeAmount.Should().Be(250m);
    }

    [Fact]
    public async Task GetVersionAsync_returns_specific_row_by_id()
    {
        var doc = await _repo.CreateAsync(Sample("row-spec", "CN-SPEC"));

        var fetched = await _repo.GetVersionAsync(doc.ClaimVersionId, doc.Id);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(doc.Id);
        fetched.ClaimVersionId.Should().Be(doc.ClaimVersionId);
    }

    [Fact]
    public async Task GetVersionAsync_returns_null_for_unknown_row()
    {
        var doc = await _repo.CreateAsync(Sample("row-known", "CN-K"));

        var fetched = await _repo.GetVersionAsync(doc.ClaimVersionId, "no-such-row");
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestVersionAsync_returns_head_version_in_effect_at_asOf()
    {
        var v1 = await _repo.CreateAsync(BuildVersion("chain-multi", "row-1", n: 1, ClaimVersionState.Adjudicated, publishedAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)));
        var v2 = BuildVersion("chain-multi", "row-2", n: 2, ClaimVersionState.Submitted, publishedAt: new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));
        await _repo.CreateAsync(v2);

        // asOf in mid-Jan: only v1 is in effect (v2 hasn't been published yet).
        var jan15 = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        var head = await _repo.GetLatestVersionAsync("chain-multi", jan15);
        head.Should().NotBeNull();
        head!.Id.Should().Be("row-1");

        // asOf in Mar: v2 is now the head.
        var mar1 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        head = await _repo.GetLatestVersionAsync("chain-multi", mar1);
        head!.Id.Should().Be("row-2");
    }

    [Fact]
    public async Task ListVersionsAsync_returns_newest_first()
    {
        await _repo.CreateAsync(BuildVersion("chain-list", "row-1", n: 1, ClaimVersionState.Adjudicated));
        await _repo.CreateAsync(BuildVersion("chain-list", "row-2", n: 2, ClaimVersionState.Submitted));
        await _repo.CreateAsync(BuildVersion("chain-list", "row-3", n: 3, ClaimVersionState.Submitted));

        var (items, _) = await _repo.ListVersionsAsync("chain-list", pageSize: 10, continuationToken: null);

        items.Should().HaveCount(3);
        items[0].VersionNumber.Should().Be(3);
        items[1].VersionNumber.Should().Be(2);
        items[2].VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_patches_head_without_rolling_new_version()
    {
        // Create a Submitted head, then patch it. Version count should NOT
        // change. This is the 5th instance of the projection-metadata
        // bypass pattern.
        var v1 = await _repo.CreateAsync(BuildVersion("chain-bypass", "row-1", n: 1, ClaimVersionState.Submitted));
        v1.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "99213", Units = 1, ChargeAmount = 100m,
            ServiceDateFrom = v1.ServiceDateFrom, ServiceDateTo = v1.ServiceDateTo
        });
        await _repo.UpdateAsync(v1);

        var adjudication = new AdjudicationResult
        {
            NetworkTier = "InNetwork",
            AllowedAmount = 80m,
            DeductibleAmount = 0m,
            CoinsuranceAmount = 16m,
            CopayAmount = 0m,
            PatientResponsibility = 16m,
            PayerPayment = 64m
        };
        var lineResults = new List<LineAdjudicationResult>
        {
            new() { AllowedAmount = 80m, PaidAmount = 64m, PatientResponsibility = 16m }
        };

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-bypass", adjudication, lineResults);

        ok.Should().BeTrue();

        var (versions, _) = await _repo.ListVersionsAsync("chain-bypass", pageSize: 10, continuationToken: null);
        versions.Should().HaveCount(1, "the bypass writes onto the head, not a new row");
        versions[0].AdjudicationResult.Should().NotBeNull();
        versions[0].AdjudicationResult!.AllowedAmount.Should().Be(80m);
        versions[0].ClaimLines[0].AdjudicationResult.Should().NotBeNull();
        versions[0].ClaimLines[0].AdjudicationResult!.PaidAmount.Should().Be(64m);
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_returns_false_for_unknown_chain()
    {
        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "no-such-chain",
            new AdjudicationResult(),
            Array.Empty<LineAdjudicationResult>());

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_skips_terminal_versions()
    {
        // The bypass only writes to non-terminal head rows (Submitted or
        // Adjudicated). Voided/Paid rows must be untouched.
        await _repo.CreateAsync(BuildVersion("chain-voided", "row-1", n: 1, ClaimVersionState.Voided));

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-voided",
            new AdjudicationResult { AllowedAmount = 999m },
            Array.Empty<LineAdjudicationResult>());

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task GetAccumulatorTotalsAsync_includes_versionState_finalized_rows()
    {
        // Versioned rows with VersionState=Adjudicated should count toward
        // accumulator totals even when ClaimStatus stays at Approved (the
        // ClaimVersionState filter is the new, correct path).
        var doc = BuildVersion("chain-accum", "row-1", n: 1, ClaimVersionState.Adjudicated);
        doc.BenefitPlanId = "plan-A";
        doc.MemberId = "owner-M";
        doc.Status = ClaimStatus.Approved;
        doc.AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = "InNetwork",
            DeductibleAmount = 100m,
            CoinsuranceAmount = 50m,
            CopayAmount = 25m,
            PatientResponsibility = 175m,
            PayerPayment = 825m
        };
        await _repo.CreateAsync(doc);

        var totals = await _repo.GetAccumulatorTotalsAsync(
            "owner-M", "Individual", "plan-A", "2026");

        totals.Totals.Should().NotBeEmpty();
        totals.Totals.Should().Contain(t =>
            t.AccumulatorType == "IndividualDeductible" && t.NetworkTier == "InNetwork" && t.AccumulatedAmount == 100m);
        totals.Totals.Should().Contain(t =>
            t.AccumulatorType == "IndividualOutOfPocketMax" && t.NetworkTier == "InNetwork" && t.AccumulatedAmount == 175m);
    }

    [Fact]
    public async Task GetAccumulatorTotalsAsync_includes_legacy_rows_via_status_clause()
    {
        // Pre-versioning Mongo rows still have Status=Paid; the OR clause
        // keeps them visible while the chain is rolled out.
        var collection = _database.GetCollection<Claim>("Claims");
        var legacy = Sample("legacy-paid");
        legacy.ClaimVersionId = string.Empty;        // legacy: no chain
        legacy.VersionNumber = 0;                    // legacy: no version
        legacy.VersionState = ClaimVersionState.Unknown; // legacy: no state
        legacy.BenefitPlanId = "plan-A";
        legacy.MemberId = "owner-L";
        legacy.Status = ClaimStatus.Paid;
        legacy.AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = "InNetwork",
            DeductibleAmount = 50m,
            CoinsuranceAmount = 0m,
            CopayAmount = 0m,
            PatientResponsibility = 50m,
            PayerPayment = 200m
        };
        await collection.InsertOneAsync(legacy);

        var totals = await _repo.GetAccumulatorTotalsAsync(
            "owner-L", "Individual", "plan-A", "2026");

        totals.Totals.Should().Contain(t =>
            t.AccumulatorType == "IndividualDeductible" && t.AccumulatedAmount == 50m);
    }

    [Fact]
    public async Task GetAccumulatorTotalsAsync_excludes_draft_and_submitted_rows()
    {
        var draft = BuildVersion("chain-draft", "row-1", n: 1, ClaimVersionState.Draft);
        draft.BenefitPlanId = "plan-A";
        draft.MemberId = "owner-D";
        draft.Status = ClaimStatus.Submitted;
        draft.AdjudicationResult = new AdjudicationResult
        {
            NetworkTier = "InNetwork", DeductibleAmount = 999m, PatientResponsibility = 999m
        };
        await _repo.CreateAsync(draft);

        var totals = await _repo.GetAccumulatorTotalsAsync(
            "owner-D", "Individual", "plan-A", "2026");

        totals.Totals.Should().BeEmpty("Draft and Submitted versions don't count toward accumulators");
    }

    private Claim BuildVersion(string chainKey, string rowId, int n, ClaimVersionState state, DateTime? publishedAt = null)
    {
        var doc = Sample(rowId, $"CN-{rowId}");
        doc.ClaimVersionId = chainKey;
        doc.VersionNumber = n;
        doc.VersionState = state;
        doc.PublishedAt = publishedAt;
        return doc;
    }
}
