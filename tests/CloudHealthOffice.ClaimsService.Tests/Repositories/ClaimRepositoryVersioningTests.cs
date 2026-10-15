using ClaimsService.Exceptions;
using ClaimsService.Models;
using ClaimsService.Repositories;
using ClaimsService.Services.Adjudication;
using CloudHealthOffice.Testing.Mongo;
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
[Collection(MongoRunnerFixture.CollectionName)]
public class ClaimRepositoryVersioningTests : IAsyncLifetime
{
    private const string Tenant = "tenant-claims";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private ClaimRepositoryMongo _repo = null!;
    private DefaultHttpContext _ctx = null!;

    public ClaimRepositoryVersioningTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("claim_repo_test");
        _ctx = new DefaultHttpContext();
        _ctx.Items["TenantId"] = Tenant;
        var accessor = new HttpContextAccessor { HttpContext = _ctx };
        _repo = new ClaimRepositoryMongo(_database, accessor, NullLogger<ClaimRepositoryMongo>.Instance);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

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

    [Fact]
    public async Task ApprovedRow_with_empty_claim_financials_hydrates_from_line_adjudication()
    {
        var collection = _database.GetCollection<Claim>("Claims");
        var doc = Sample("legacy-approved-financials");
        doc.Status = ClaimStatus.Approved;
        doc.AdjudicationResult = new AdjudicationResult
        {
            DenialReasonCode = "96",
            DenialReason = "Stale denial projection"
        };
        doc.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1,
            ProcedureCode = "99203",
            Units = 1,
            ChargeAmount = 191m,
            ServiceDateFrom = doc.ServiceDateFrom,
            ServiceDateTo = doc.ServiceDateTo,
            AdjudicationResult = new LineAdjudicationResult
            {
                AllowedAmount = 191m,
                PaidAmount = 161m,
                PatientResponsibility = 30m
            }
        });
        await collection.InsertOneAsync(doc);

        var hydrated = await _repo.GetByIdAsync(doc.Id);

        hydrated.Should().NotBeNull();
        hydrated!.AdjudicationResult.Should().NotBeNull();
        hydrated.AdjudicationResult!.AllowedAmount.Should().Be(191m);
        hydrated.AdjudicationResult.PayerPayment.Should().Be(161m);
        hydrated.AdjudicationResult.PatientResponsibility.Should().Be(30m);
        hydrated.AdjudicationResult.DenialReasonCode.Should().BeNull();
        hydrated.AdjudicationResult.DenialReason.Should().BeNull();
    }

    [Fact]
    public async Task PartiallyPaidRow_with_existing_claim_financials_hydrates_without_replacing_amounts_but_clears_denial()
    {
        var collection = _database.GetCollection<Claim>("Claims");
        var doc = Sample("legacy-partially-paid-financials");
        doc.Status = ClaimStatus.PartiallyPaid;
        doc.AdjudicationResult = new AdjudicationResult
        {
            AllowedAmount = 500m,
            DeductibleAmount = 25m,
            CoinsuranceAmount = 50m,
            CopayAmount = 10m,
            PatientResponsibility = 85m,
            PayerPayment = 415m,
            DenialReasonCode = "96",
            DenialReason = "Stale denial projection"
        };
        doc.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1,
            ProcedureCode = "99203",
            Units = 1,
            ChargeAmount = 191m,
            ServiceDateFrom = doc.ServiceDateFrom,
            ServiceDateTo = doc.ServiceDateTo,
            AdjudicationResult = new LineAdjudicationResult
            {
                AllowedAmount = 191m,
                PaidAmount = 161m,
                PatientResponsibility = 30m
            }
        });
        await collection.InsertOneAsync(doc);

        var hydrated = await _repo.GetByIdAsync(doc.Id);

        hydrated.Should().NotBeNull();
        hydrated!.AdjudicationResult.Should().NotBeNull();
        hydrated.AdjudicationResult!.AllowedAmount.Should().Be(500m);
        hydrated.AdjudicationResult.DeductibleAmount.Should().Be(25m);
        hydrated.AdjudicationResult.CoinsuranceAmount.Should().Be(50m);
        hydrated.AdjudicationResult.CopayAmount.Should().Be(10m);
        hydrated.AdjudicationResult.PatientResponsibility.Should().Be(85m);
        hydrated.AdjudicationResult.PayerPayment.Should().Be(415m);
        hydrated.AdjudicationResult.DenialReasonCode.Should().BeNull();
        hydrated.AdjudicationResult.DenialReason.Should().BeNull();
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

    [Theory]
    [InlineData(ClaimStatus.Submitted, false)]
    [InlineData(ClaimStatus.Received, false)]
    [InlineData(ClaimStatus.InAdjudication, false)]
    [InlineData(ClaimStatus.Pended, false)] // not final — re-pending must be allowed
    [InlineData(ClaimStatus.Approved, true)]
    [InlineData(ClaimStatus.Denied, true)]
    [InlineData(ClaimStatus.Paid, true)]
    [InlineData(ClaimStatus.PartiallyPaid, true)]
    [InlineData(ClaimStatus.Voided, true)]
    public void IsFinalDisposition_truth_table_is_pinned(ClaimStatus status, bool expectedFinal)
    {
        // Shared by ClaimRepository (Cosmos) and ClaimRepositoryMongo so both
        // backends apply the identical Pend-projection precedence rule; pin
        // it here so the table is reviewed if anyone changes it.
        ClaimRepository.IsFinalDisposition(status).Should().Be(expectedFinal);
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
    public async Task GetLatestVersionAsync_uses_adjudication_tenant_context_without_http_context()
    {
        var created = await _repo.CreateAsync(Sample("row-background"));
        var tenantContext = new AdjudicationTenantContext { TenantId = Tenant };
        var backgroundRepo = new ClaimRepositoryMongo(
            _database,
            new HttpContextAccessor(),
            NullLogger<ClaimRepositoryMongo>.Instance,
            tenantContext);

        var latest = await backgroundRepo.GetLatestVersionAsync(created.ClaimVersionId, DateTime.UtcNow);

        latest.Should().NotBeNull();
        latest!.Id.Should().Be(created.Id);
        latest.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task ListVersionsAsync_returns_newest_first()
    {
        await _repo.CreateAsync(BuildVersion("chain-list", "row-1", n: 1, ClaimVersionState.Denied));
        await _repo.CreateAsync(BuildVersion("chain-list", "row-2", n: 2, ClaimVersionState.Draft));
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
    public async Task UpdateAdjudicationProjectionAsync_ignores_newer_draft_versions()
    {
        var submitted = await _repo.CreateAsync(BuildVersion("chain-draft-head", "row-1", n: 1, ClaimVersionState.Submitted));
        submitted.ClaimLines.Add(new ClaimLine
        {
            LineNumber = 1, ProcedureCode = "99213", Units = 1, ChargeAmount = 100m,
            ServiceDateFrom = submitted.ServiceDateFrom, ServiceDateTo = submitted.ServiceDateTo
        });
        await _repo.UpdateAsync(submitted);
        await _repo.CreateAsync(BuildVersion("chain-draft-head", "row-2", n: 2, ClaimVersionState.Draft));

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-draft-head",
            new AdjudicationResult { AllowedAmount = 80m, PayerPayment = 64m },
            new[] { new LineAdjudicationResult { AllowedAmount = 80m, PaidAmount = 64m, PatientResponsibility = 16m } });

        ok.Should().BeTrue();

        var submittedVersion = await _repo.GetVersionAsync("chain-draft-head", "row-1");
        submittedVersion!.AdjudicationResult!.PayerPayment.Should().Be(64m);
        submittedVersion.ClaimLines[0].AdjudicationResult!.PaidAmount.Should().Be(64m);

        var draftVersion = await _repo.GetVersionAsync("chain-draft-head", "row-2");
        draftVersion!.AdjudicationResult.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Pend-persistence defect fix: isPend projects ClaimStatus.Pended
    // (Defect A). Non-pend behavior and the terminal-disposition
    // precedence rule are pinned here too.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_withIsPendTrue_setsStatusPendedAndPendDetails()
    {
        var head = await _repo.CreateAsync(BuildVersion("chain-pend-ncci", "row-1", n: 1, ClaimVersionState.Submitted));
        head.Status.Should().Be(ClaimStatus.Submitted, "the fixture starts pre-adjudication");

        var pendDetails = new PendDetails { PendCode = "NCCI", PendReason = "bundled pair NE001" };
        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-pend-ncci",
            new AdjudicationResult { AllowedAmount = 0m },
            Array.Empty<LineAdjudicationResult>(),
            pendDetails: pendDetails,
            isPend: true);

        ok.Should().BeTrue();
        var reread = await _repo.GetVersionAsync("chain-pend-ncci", "row-1");
        reread!.Status.Should().Be(ClaimStatus.Pended);
        reread.PendDetails.Should().NotBeNull();
        reread.PendDetails!.PendCode.Should().Be("NCCI");
        reread.PendDetails.PendReason.Should().Be("bundled pair NE001");
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_withoutResolvedStatus_leavesStatusUnchanged()
    {
        // Backward-compatibility guard: existing callers that only project
        // financial metadata and omit resolvedStatus must not accidentally
        // infer a claim status from the AdjudicationResult shape.
        var head = await _repo.CreateAsync(BuildVersion("chain-pass", "row-1", n: 1, ClaimVersionState.Submitted));
        head.Status.Should().Be(ClaimStatus.Submitted);

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-pass",
            new AdjudicationResult { AllowedAmount = 150m, PayerPayment = 120m },
            Array.Empty<LineAdjudicationResult>(),
            isPend: false);

        ok.Should().BeTrue();
        var reread = await _repo.GetVersionAsync("chain-pass", "row-1");
        reread!.Status.Should().Be(ClaimStatus.Submitted, "resolvedStatus was not supplied");
        reread.AdjudicationResult.Should().NotBeNull("the non-status projection fields still write as before");
        reread.AdjudicationResult!.PayerPayment.Should().Be(120m);
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_withResolvedStatus_setsTerminalStatusAndVersionState()
    {
        var head = await _repo.CreateAsync(BuildVersion("chain-deny", "row-1", n: 1, ClaimVersionState.Submitted));
        head.Status.Should().Be(ClaimStatus.Submitted);

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-deny",
            new AdjudicationResult { AllowedAmount = 0m, DenialReasonCode = "999" },
            Array.Empty<LineAdjudicationResult>(),
            isPend: false,
            resolvedStatus: ClaimStatus.Denied);

        ok.Should().BeTrue();
        var reread = await _repo.GetVersionAsync("chain-deny", "row-1");
        reread!.Status.Should().Be(ClaimStatus.Denied);
        reread.VersionState.Should().Be(ClaimVersionState.Denied);
        reread.AdjudicationResult.Should().NotBeNull("the financial projection still writes with the terminal status");
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_withDeniedEvidence_repairsApprovedStatusRace()
    {
        // Regression for async pipeline races where the synchronous summary
        // projected Approved first, then the async adjudication projection
        // wrote denial evidence. The guarded status patch must repair the
        // impossible Approved + denial-evidence state without weakening pend
        // protection.
        var head = BuildVersion("chain-approved-denial-race", "row-1", n: 1, ClaimVersionState.Adjudicated);
        head.Status = ClaimStatus.Approved;
        head.AdjudicationResult = new AdjudicationResult { AllowedAmount = 100m, PayerPayment = 80m };
        await _repo.CreateAsync(head);

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant,
            "chain-approved-denial-race",
            new AdjudicationResult
            {
                AllowedAmount = 0m,
                PayerPayment = 0m,
                DenialReasonCode = "197",
                DenialReason = "Authorization expired or not yet active"
            },
            Array.Empty<LineAdjudicationResult>(),
            isPend: false,
            resolvedStatus: ClaimStatus.Denied);

        ok.Should().BeTrue();
        var reread = await _repo.GetVersionAsync("chain-approved-denial-race", "row-1");
        reread!.Status.Should().Be(ClaimStatus.Denied);
        reread.VersionState.Should().Be(ClaimVersionState.Denied);
        reread.AdjudicationResult.Should().NotBeNull();
        reread.AdjudicationResult!.DenialReasonCode.Should().Be("197");
        reread.AdjudicationResult.DenialReason.Should().Be("Authorization expired or not yet active");
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_withIsPendTrue_onAlreadyApprovedClaim_doesNotDowngradeStatus()
    {
        // Precedence rule (task requirement A.3): a claim that already
        // reached a later-stage disposition — e.g. because the Argo
        // workflow's synchronous finalize step, or an examiner override,
        // raced ahead of this async projection — must never be downgraded
        // back to Pended. Approved maps to VersionState.Adjudicated, which
        // is NOT terminal for VersionState purposes (re-adjudication is
        // allowed), so this precedence check is the only thing protecting
        // ClaimStatus here.
        var head = BuildVersion("chain-already-approved", "row-1", n: 1, ClaimVersionState.Adjudicated);
        head.Status = ClaimStatus.Approved;
        await _repo.CreateAsync(head);

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-already-approved",
            new AdjudicationResult { AllowedAmount = 100m },
            Array.Empty<LineAdjudicationResult>(),
            pendDetails: new PendDetails { PendCode = "NCCI", PendReason = "late-arriving pend" },
            isPend: true);

        ok.Should().BeTrue("the write itself still succeeds — only the /status patch is skipped");
        var reread = await _repo.GetVersionAsync("chain-already-approved", "row-1");
        reread!.Status.Should().Be(ClaimStatus.Approved, "a later-stage disposition must never be downgraded back to Pended");
    }

    [Fact]
    public async Task UpdateAdjudicationProjectionAsync_withIsPendTrue_onAlreadyPendedClaim_refreshesPendDetails()
    {
        // Pended is NOT a final disposition — a re-adjudication run that
        // pends again (e.g. a new NCCI edit-failure set) must be allowed to
        // refresh PendDetails and re-affirm Status=Pended.
        var head = BuildVersion("chain-repend", "row-1", n: 1, ClaimVersionState.Submitted);
        head.Status = ClaimStatus.Pended;
        head.PendDetails = new PendDetails { PendCode = "NCCI", PendReason = "first pend" };
        await _repo.CreateAsync(head);

        var ok = await _repo.UpdateAdjudicationProjectionAsync(
            Tenant, "chain-repend",
            new AdjudicationResult { AllowedAmount = 0m },
            Array.Empty<LineAdjudicationResult>(),
            pendDetails: new PendDetails { PendCode = "MUE", PendReason = "second pend, different edit" },
            isPend: true);

        ok.Should().BeTrue();
        var reread = await _repo.GetVersionAsync("chain-repend", "row-1");
        reread!.Status.Should().Be(ClaimStatus.Pended);
        reread.PendDetails!.PendCode.Should().Be("MUE");
        reread.PendDetails.PendReason.Should().Be("second pend, different edit");
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

    // ═══════════════════════════════════════════════════════════════════
    // Residual-race fix — TryTransitionStatusAsync (and the pipeline's
    // resolved-status projection) never overwrite a persisted Pended (or
    // already-final) status.
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(ClaimStatus.Submitted, false)]
    [InlineData(ClaimStatus.Received, false)]
    [InlineData(ClaimStatus.InAdjudication, false)]
    [InlineData(ClaimStatus.Pended, true)] // the residual-race fix's headline addition
    [InlineData(ClaimStatus.Approved, true)]
    [InlineData(ClaimStatus.Denied, true)]
    [InlineData(ClaimStatus.Paid, true)]
    [InlineData(ClaimStatus.PartiallyPaid, true)]
    [InlineData(ClaimStatus.Voided, true)]
    public void BlocksSynchronousWriteback_truth_table_is_pinned(ClaimStatus status, bool expectedBlocked)
    {
        // Shared by ClaimRepository (Cosmos) and ClaimRepositoryMongo so both
        // backends apply the identical synchronous-writeback precedence rule;
        // pin it here so the table is reviewed if anyone changes it. Note
        // this is a DIFFERENT set than IsFinalDisposition (Pended is
        // included here, excluded there) — see that method's doc comment.
        ClaimRepository.BlocksSynchronousWriteback(status).Should().Be(expectedBlocked);
    }

    [Fact]
    public void CanRepairContradictoryDeniedSummary_only_allows_paid_denial_without_denial_evidence()
    {
        var paidSummary = new AdjudicationResult { PayerPayment = 150m };

        ClaimRepository.CanRepairContradictoryDeniedSummary(
                ClaimStatus.Denied,
                ClaimStatus.Approved,
                paidSummary)
            .Should().BeTrue();

        ClaimRepository.CanRepairContradictoryDeniedSummary(
                ClaimStatus.Pended,
                ClaimStatus.Approved,
                paidSummary)
            .Should().BeFalse("pends remain protected from synchronous summary writeback");

        ClaimRepository.CanRepairContradictoryDeniedSummary(
                ClaimStatus.Denied,
                ClaimStatus.Approved,
                new AdjudicationResult { PayerPayment = 150m, DenialReasonCode = "96" })
            .Should().BeFalse("an incoming summary with denial evidence is not a paid contradiction");
    }

    [Fact]
    public void CanRepairContradictoryApprovedSummary_only_allows_zeroPay_denial_with_evidence()
    {
        var deniedSummary = new AdjudicationResult
        {
            PayerPayment = 0m,
            DenialReasonCode = "197",
            DenialReason = "Prior authorization required"
        };

        ClaimRepository.CanRepairContradictoryApprovedSummary(
                ClaimStatus.Approved,
                ClaimStatus.Denied,
                deniedSummary)
            .Should().BeTrue();

        ClaimRepository.CanRepairContradictoryApprovedSummary(
                ClaimStatus.Pended,
                ClaimStatus.Denied,
                deniedSummary)
            .Should().BeFalse("pends remain protected from synchronous summary writeback");

        ClaimRepository.CanRepairContradictoryApprovedSummary(
                ClaimStatus.Approved,
                ClaimStatus.Denied,
                new AdjudicationResult { PayerPayment = 0m })
            .Should().BeFalse("zero payment without denial evidence is not enough to reopen a final status");
    }

    /// <summary>
    /// #1299: the live-row check that replaced the Cosmos repair filter
    /// predicates (the emulator could not evaluate them). A Pended row is
    /// never repaired; a row whose lock moved on is never repaired.
    /// </summary>
    [Theory]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Denied, 0, "197", null, true)]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Denied, 0, null, null, false)]
    [InlineData(ClaimStatus.Pended, ClaimStatus.Denied, 0, "197", null, false)]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Denied, 10, "197", null, false)]
    [InlineData(ClaimStatus.Denied, ClaimStatus.Approved, 150, null, null, true)]
    [InlineData(ClaimStatus.Denied, ClaimStatus.Approved, 150, "96", null, false)]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Denied, 0, "197", "other-token", false)]
    [InlineData(ClaimStatus.Approved, ClaimStatus.Denied, 0, "197", "my-token", true)]
    public void IsRepairableContradiction_ChecksTheLiveRow(
        ClaimStatus rowStatus, ClaimStatus desired, int payerPayment, string? denialCode, string? lockOnRow, bool expected)
    {
        var row = new Claim
        {
            Status = rowStatus,
            AdjudicationResult = new AdjudicationResult { PayerPayment = payerPayment, DenialReasonCode = denialCode },
            ResolutionLock = lockOnRow is null ? null : new ExaminerResolutionLock { Token = lockOnRow },
        };

        ClaimRepository.IsRepairableContradiction(row, desired, lockOnRow is null ? null : "my-token")
            .Should().Be(expected);
    }

    /// <summary>A stored row may deserialize "adjustmentReasons": null; the check must not throw.</summary>
    [Fact]
    public void IsRepairableContradiction_NullAdjustmentReasons_DoesNotThrow()
    {
        var row = new Claim
        {
            Status = ClaimStatus.Approved,
            AdjudicationResult = new AdjudicationResult { PayerPayment = 0m, AdjustmentReasons = null! },
        };

        ClaimRepository.IsRepairableContradiction(row, ClaimStatus.Denied, null).Should().BeFalse();
    }

    [Fact]
    public void IsRepairableContradiction_AdjustmentReasonsAreDenialEvidence()
    {
        var row = new Claim { Status = ClaimStatus.Approved, AdjudicationResult = new AdjudicationResult { PayerPayment = 0m } };
        row.AdjudicationResult.AdjustmentReasons.Add(new ClaimAdjustmentReason { GroupCode = "CO", ReasonCode = "50", Amount = 10m });

        ClaimRepository.IsRepairableContradiction(row, ClaimStatus.Denied, null).Should().BeTrue();
    }

    [Fact]
    public async Task TryTransitionStatusAsync_onNonPendedClaim_appliesStatus()
    {
        var doc = await _repo.CreateAsync(Sample("row-status-normal"));

        var result = await _repo.TryTransitionStatusAsync(Tenant, "row-status-normal", ClaimStatus.Denied);

        result.Outcome.Should().Be(StatusWriteOutcome.Applied);
        var reread = await _repo.GetByIdAsync("row-status-normal");
        reread!.Status.Should().Be(ClaimStatus.Denied);
        reread.VersionState.Should().Be(ClaimVersionState.Denied);
    }

    [Fact]
    public async Task TryTransitionStatusAsync_onAlreadyPendedClaim_suppressesTransition()
    {
        // Backs PUT /{id}/status, which can race the async orchestrator's
        // own Pend projection for the same claim.
        var doc = Sample("row-status-pended");
        doc.Status = ClaimStatus.Pended;
        await _repo.CreateAsync(doc);

        var result = await _repo.TryTransitionStatusAsync(Tenant, "row-status-pended", ClaimStatus.Approved);

        result.Outcome.Should().Be(StatusWriteOutcome.Suppressed);
        result.PersistedStatus.Should().Be(ClaimStatus.Pended);
        var reread = await _repo.GetByIdAsync("row-status-pended");
        reread!.Status.Should().Be(ClaimStatus.Pended);
    }

    [Fact]
    public async Task TryTransitionStatusAsync_forUnknownClaim_returnsNotFound()
    {
        var result = await _repo.TryTransitionStatusAsync(Tenant, "no-such-row", ClaimStatus.Approved);
        result.Outcome.Should().Be(StatusWriteOutcome.NotFound);
    }
}
