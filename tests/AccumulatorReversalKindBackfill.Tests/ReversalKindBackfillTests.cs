using AccumulatorReversalKindBackfill;
using AccumulatorService.Models;
using AccumulatorService.Repositories;
using AccumulatorService.Services;
using CloudHealthOffice.Events;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Svc = AccumulatorService.Services.AccumulatorService;

namespace AccumulatorReversalKindBackfill.Tests;

/// <summary>
/// tools/AccumulatorReversalKindBackfill on a real mongod. The legacy data is made by the real
/// accumulator-service (applies, replacements, voids, denials, a replacement overtaking its
/// original), then has its <c>ReversalKind</c> removed — exactly what a database written before
/// the kind was recorded holds. claims-service evidence is a <c>Claims</c> collection in a
/// second database.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class ReversalKindBackfillTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "t1";
    private const string Member = "m-1";
    private static readonly DateTime YearStart = new(2026, 1, 1);
    private IMongoDatabase _db = null!;
    private IMongoDatabase _claimsDb = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("acc_kind_backfill");
        _claimsDb = mongo.CreateDatabase("acc_kind_claims");
        _dir = Path.Combine(Path.GetTempPath(), "acc-kind-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        await AccumulatorMongoIndexes.EnsureAsync(_db, NullLogger.Instance);
        await new AccumulatorRepositoryMongo(_db).TryReplaceSnapshotAsync(new AccumulatorSnapshot
        {
            Id = AccumulatorSnapshot.BuildId(Tenant, Member, YearStart),
            TenantId = Tenant,
            MemberId = Member,
            PlanYearStart = YearStart,
            PlanYearEnd = new DateTime(2026, 12, 31),
            IndividualDeductibleLimit = 5000m,
            IndividualOopLimit = 9000m,
        }, expectedVersion: 0);

        var svc = Service();
        // R: C1 replaced by C2 (frequency 7).
        await svc.ApplyClaimFinalizedAsync(Claim("C1", 100m));
        await svc.ApplyClaimFinalizedAsync(Claim("C2", 120m, frequency: "7", original: "C1"));
        // V: C3 voided by V3 (frequency 8).
        await svc.ApplyClaimFinalizedAsync(Claim("C3", 200m));
        await svc.ApplyClaimFinalizedAsync(Claim("V3", 200m, status: "Reversed", frequency: "8", original: "C3"));
        // O: C4's own void.
        await svc.ApplyClaimFinalizedAsync(Claim("C4", 30m));
        await svc.ApplyClaimFinalizedAsync(Claim("C4", 30m, status: "Reversed"));
        // D: C5 applied, then finalized Denied.
        await svc.ApplyClaimFinalizedAsync(Claim("C5", 40m));
        await svc.ApplyClaimFinalizedAsync(Claim("C5", 40m, status: "Denied"));
        // T: C7 replaces C6 and overtakes it; C6 then arrives and is skipped.
        await svc.ApplyClaimFinalizedAsync(Claim("C7", 70m, frequency: "7", original: "C6"));
        await svc.ApplyClaimFinalizedAsync(Claim("C6", 60m));
        // A: V8 (frequency 8) voids C8, but V8 also has a marker of its own: contradictory.
        await svc.ApplyClaimFinalizedAsync(Claim("C8", 80m));
        await svc.ApplyClaimFinalizedAsync(Claim("V8", 80m, status: "Reversed", frequency: "8", original: "C8"));
        await Store().BeginLeaseAsync(Tenant, "V8");
        await Store().CompleteAsync(Tenant, "V8", "x", "Applied");

        await StripKindsAsync();

        var claims = _claimsDb.GetCollection<BsonDocument>("Claims");
        await claims.InsertManyAsync(
        [
            ClaimDoc("C2", 5, "7", "C1"),
            ClaimDoc("V3", 8, "8", "C3"),
            ClaimDoc("C4", 8, "1", null),
            ClaimDoc("C5", 6, "1", null),
            ClaimDoc("C7", 5, "7", "C6"),
            ClaimDoc("V8", 8, "8", "C8"),
        ]);
    }

    public async Task DisposeAsync()
    {
        await mongo.DropDatabaseAsync(_db);
        await mongo.DropDatabaseAsync(_claimsDb);
        Directory.Delete(_dir, recursive: true);
    }

    private ProcessedClaimStoreMongo Store() => new(_db);

    private Svc Service() => new(new AccumulatorRepositoryMongo(_db), Store(), new NullPublisher(), NullLogger<Svc>.Instance);

    private static ClaimFinalizedEvent Claim(
        string claimId, decimal deductible, string status = "Paid", string? frequency = null, string? original = null) => new()
    {
        TenantId = Tenant, ClaimId = claimId, ClaimNumber = claimId, MemberId = Member,
        ServiceDate = new DateTime(2026, 3, 15), BenefitCategory = "OV", FinalStatus = status,
        ClaimFrequencyCode = frequency, OriginalClaimId = original,
        DeductibleApplied = deductible, OopApplied = deductible, MemberResponsibility = deductible,
    };

    private static BsonDocument ClaimDoc(string id, int status, string frequency, string? predecessor) => new()
    {
        { "_id", id }, { "TenantId", Tenant }, { "Status", status }, { "ClaimFrequencyCode", frequency },
        { "PredecessorVersionId", predecessor is null ? BsonNull.Value : predecessor },
    };

    private IMongoCollection<AccumulatorEvent> Events => _db.GetCollection<AccumulatorEvent>(AccumulatorMongoIndexes.EventsCollection);
    private IMongoCollection<ProcessedClaim> Markers => _db.GetCollection<ProcessedClaim>(AccumulatorMongoIndexes.ProcessedClaimsCollection);

    /// <summary>What a database written before the kind was recorded holds.</summary>
    private async Task StripKindsAsync()
    {
        await Events.UpdateManyAsync(Builders<AccumulatorEvent>.Filter.In(e => e.EventType, new[] { "ClaimReversed", "ClaimTombstoned" }),
            Builders<AccumulatorEvent>.Update.Unset(e => e.ReversalKind));
        await Markers.UpdateManyAsync(Builders<ProcessedClaim>.Filter.Ne(p => p.ReversalKind, null),
            Builders<ProcessedClaim>.Update.Unset(p => p.ReversalKind));
    }

    private Target Target(bool withClaims = true) => new(
        mongo.Client, _db.DatabaseNamespace.DatabaseName,
        withClaims ? new ClaimsEvidence(mongo.Client, _claimsDb.DatabaseNamespace.DatabaseName, useTenantScoping: false) : null);

    private string DatabaseName => _db.DatabaseNamespace.DatabaseName;

    private async Task<string> ListToFileAsync(Target target, string name = "plan.csv")
    {
        var backfill = new Backfill(target, _ => { });
        var rows = await backfill.ListAsync([]);
        var path = Path.Combine(_dir, name);
        await using (var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false)))
            await backfill.WriteCsvAsync(writer, rows);
        return path;
    }

    private Task<List<RowResult>> ApplyAsync(string csv, bool execute, string? sha = null, string op = "operator-1", Target? target = null) =>
        new Backfill(target ?? Target(), _ => { }).ApplyAsync(new ApplyOptions
        {
            CsvPath = csv,
            Execute = execute,
            ExpectedSha256 = execute ? sha ?? Backfill.Sha256Of(csv) : null,
            Operator = op,
            ConfirmDatabase = DatabaseName,
        });

    private async Task<Dictionary<string, string>> KindsByOriginalAsync()
    {
        var rows = await new Backfill(Target(), _ => { }).ListAsync([]);
        return rows.GroupBy(r => r.Item.OriginalClaimId).ToDictionary(g => g.Key, g => g.First().Classification.Kind);
    }

    private async Task<string?> RowKind(string original) =>
        (await Events.Find(e => e.EventType == "ClaimReversed" && e.SourceClaimId == original).SingleAsync()).ReversalKind;

    // ── list ──────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ClassifiesEachOriginal_FromItsEvidence()
    {
        var rows = await new Backfill(Target(), _ => { }).ListAsync([]);
        var kinds = rows.GroupBy(r => r.Item.OriginalClaimId).ToDictionary(g => g.Key, g => g.Select(r => r.Classification).Distinct().Single());

        Assert.Equal(ReversalKinds.Replacement, kinds["C1"].Kind);
        Assert.Contains("frequency 7", kinds["C1"].Evidence);
        Assert.Equal(ReversalKinds.Void, kinds["C3"].Kind);
        Assert.Contains("frequency 8", kinds["C3"].Evidence);
        Assert.Contains("never processed as a claim", kinds["C3"].Evidence);
        Assert.Equal(ReversalKinds.Void, kinds["C4"].Kind);
        Assert.Equal(ReversalKinds.Denial, kinds["C5"].Kind);
        Assert.Equal(ReversalKinds.Replacement, kinds["C6"].Kind); // the overtaking replacement's tombstones
        Assert.Equal(Classification.Ambiguous, kinds["C8"].Kind);
        Assert.Contains("a frequency-8 void never is", kinds["C8"].Evidence);
        Assert.Contains(rows, r => r.Item.OriginalClaimId == "C6" && r.Item.ItemType == ItemTypes.OriginalTombstoneMarker);
        Assert.Contains(rows, r => r.Item.OriginalClaimId == "C6" && r.Item.ItemType == ItemTypes.ReversalMarker);
        Assert.Contains(rows, r => r.Item.OriginalClaimId == "C1" && r.Item.ItemType == ItemTypes.ClaimReversedRow);
    }

    /// <summary>Without claims-service evidence a void cannot be told from a replacement whose apply never ran: AMBIGUOUS, never Void.</summary>
    [Fact]
    public async Task List_WithoutClaimsEvidence_NeverProposesVoidOrDenial()
    {
        var rows = await new Backfill(Target(withClaims: false), _ => { }).ListAsync([]);
        var kinds = rows.GroupBy(r => r.Item.OriginalClaimId).ToDictionary(g => g.Key, g => g.First().Classification.Kind);

        Assert.Equal(ReversalKinds.Replacement, kinds["C1"]);
        Assert.Equal(ReversalKinds.Replacement, kinds["C6"]);
        Assert.All(new[] { "C3", "C4", "C5" }, c => Assert.Equal(Classification.Ambiguous, kinds[c]));
        // V8 was processed as a claim of its own: Replacement, what the legacy rule already
        // assumes (writing it changes nothing); only claims-service shows the contradiction.
        Assert.Equal(ReversalKinds.Replacement, kinds["C8"]);
    }

    // ── apply ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DryRun_IsTheDefault_AndChangesNothing()
    {
        var csv = await ListToFileAsync(Target());

        var results = await ApplyAsync(csv, execute: false);

        Assert.Contains(results, r => r.Outcome == RowOutcome.WouldSet);
        Assert.DoesNotContain(results, r => r.Outcome is RowOutcome.Set or RowOutcome.Refused or RowOutcome.Failed);
        Assert.Null(await RowKind("C3"));
        Assert.Equal(0L, await _db.GetCollection<BsonDocument>(Backfill.AuditCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        Assert.Equal(0L, await _db.GetCollection<BsonDocument>(Backfill.RunsCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Execute_WritesTheKinds_LeavesAmbiguousUntouched_AndAudits()
    {
        var csv = await ListToFileAsync(Target());

        var results = await ApplyAsync(csv, execute: true);

        Assert.DoesNotContain(results, r => r.Outcome is RowOutcome.Refused or RowOutcome.Failed);
        Assert.Equal(ReversalKinds.Replacement, await RowKind("C1"));
        Assert.Equal(ReversalKinds.Void, await RowKind("C3"));
        Assert.Equal(ReversalKinds.Void, await RowKind("C4"));
        Assert.Equal(ReversalKinds.Denial, await RowKind("C5"));
        Assert.Null(await RowKind("C8"));
        Assert.All(results.Where(r => r.Row.Item.OriginalClaimId == "C8"), r => Assert.Equal(RowOutcome.Ambiguous, r.Outcome));
        Assert.Equal(ReversalKinds.Replacement,
            (await Markers.Find(p => p.ClaimId == "C6").SingleAsync()).ReversalKind);
        Assert.Equal(ReversalKinds.Replacement,
            (await Markers.Find(p => p.ClaimId == "C6:reversal").SingleAsync()).ReversalKind);

        var audit = await _db.GetCollection<AuditRecord>(Backfill.AuditCollection).Find(FilterDefinition<AuditRecord>.Empty).ToListAsync();
        var set = results.Count(r => r.Outcome == RowOutcome.Set);
        Assert.Equal(set, audit.Count(a => a.State == "Intended"));
        Assert.Equal(set, audit.Count(a => a.State == "Completed"));
        Assert.All(audit, a => Assert.Equal("operator-1", a.Operator));
        Assert.All(audit, a => Assert.Equal(Backfill.Sha256Of(csv), a.CsvSha256));
        Assert.DoesNotContain(audit, a => a.OriginalClaimId == "C8");
        var runs = await _db.GetCollection<RunRecord>(Backfill.RunsCollection).Find(FilterDefinition<RunRecord>.Empty).ToListAsync();
        Assert.Equal(new[] { "Finished", "Started" }, runs.Select(r => r.State).OrderBy(s => s));
    }

    /// <summary>
    /// What the backfill is for: once C3's legacy row says Void, a later replacement of C3 counts
    /// (before, the row read as a replacement and the real replacement was skipped).
    /// </summary>
    [Fact]
    public async Task AfterTheBackfill_AReplacementOfAVoidedOriginal_Counts()
    {
        var csv = await ListToFileAsync(Target());
        await ApplyAsync(csv, execute: true);

        var c9 = await Service().ApplyClaimFinalizedAsync(Claim("C9", 50m, frequency: "7", original: "C3"));

        Assert.Equal(ApplyOutcome.Applied, c9.Outcome);
    }

    [Fact]
    public async Task Execute_Again_ChangesNothing()
    {
        var csv = await ListToFileAsync(Target());
        await ApplyAsync(csv, execute: true);
        var audit = _db.GetCollection<AuditRecord>(Backfill.AuditCollection);
        var before = await audit.CountDocumentsAsync(FilterDefinition<AuditRecord>.Empty);

        var again = await ApplyAsync(csv, execute: true);

        Assert.DoesNotContain(again, r => r.Outcome is RowOutcome.Set or RowOutcome.Refused or RowOutcome.Failed);
        Assert.Contains(again, r => r.Outcome == RowOutcome.AlreadySet);
        Assert.Equal(before, await audit.CountDocumentsAsync(FilterDefinition<AuditRecord>.Empty));
        // And a fresh listing has nothing left but the ambiguous original.
        Assert.Equal(new[] { "C8" }, (await KindsByOriginalAsync()).Keys);
    }

    [Fact]
    public async Task Execute_WithTheWrongHash_ChangesNothing()
    {
        var csv = await ListToFileAsync(Target());

        await Assert.ThrowsAsync<CsvHashMismatchException>(() => ApplyAsync(csv, execute: true, sha: new string('0', 64)));

        Assert.Null(await RowKind("C3"));
        Assert.Equal(0L, await _db.GetCollection<BsonDocument>(Backfill.RunsCollection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
    }

    [Fact]
    public async Task Execute_AfterTheReviewedFileWasEdited_IsAHashMismatch()
    {
        var csv = await ListToFileAsync(Target());
        var reviewed = Backfill.Sha256Of(csv);
        await File.AppendAllTextAsync(csv, string.Empty + "\n");
        var text = await File.ReadAllTextAsync(csv);
        await File.WriteAllTextAsync(csv, text.Replace(",AMBIGUOUS,", ",Void,"));

        await Assert.ThrowsAsync<CsvHashMismatchException>(() => ApplyAsync(csv, execute: true, sha: reviewed));
        Assert.Null(await RowKind("C8"));
    }

    /// <summary>A reviewer who turns AMBIGUOUS into Void and re-hashes: the live evidence still says AMBIGUOUS, so it is refused.</summary>
    [Fact]
    public async Task AKindTheEvidenceDoesNotSupport_IsRefused()
    {
        var csv = await ListToFileAsync(Target());
        await File.WriteAllTextAsync(csv, (await File.ReadAllTextAsync(csv)).Replace(",AMBIGUOUS,", ",Void,"));

        var results = await ApplyAsync(csv, execute: true);

        Assert.All(results.Where(r => r.Row.Item.OriginalClaimId == "C8"), r => Assert.Equal(RowOutcome.Refused, r.Outcome));
        Assert.Null(await RowKind("C8"));
    }

    [Fact]
    public async Task EvidenceThatChangedSinceTheListing_IsRefused()
    {
        var csv = await ListToFileAsync(Target());
        await _claimsDb.GetCollection<BsonDocument>("Claims").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", "V3"), Builders<BsonDocument>.Update.Set("ClaimFrequencyCode", "7"));

        var results = await ApplyAsync(csv, execute: true);

        Assert.All(results.Where(r => r.Row.Item.OriginalClaimId == "C3"), r => Assert.Equal(RowOutcome.Refused, r.Outcome));
        Assert.Null(await RowKind("C3"));
        Assert.Equal(ReversalKinds.Replacement, await RowKind("C1")); // the rest still applied
    }

    [Fact]
    public async Task APlanMissingOneOfAnOriginalsRecords_IsRefusedForThatOriginal()
    {
        var csv = await ListToFileAsync(Target());
        var lines = (await File.ReadAllLinesAsync(csv)).ToList();
        lines.RemoveAt(lines.FindIndex(l => l.Contains(",C6:reversal,")));
        await File.WriteAllLinesAsync(csv, lines);

        var results = await ApplyAsync(csv, execute: true);

        Assert.All(results.Where(r => r.Row.Item.OriginalClaimId == "C6"), r => Assert.Equal(RowOutcome.Refused, r.Outcome));
        Assert.Null((await Markers.Find(p => p.ClaimId == "C6").SingleAsync()).ReversalKind);
    }

    [Fact]
    public async Task Execute_RequiresAnOperator_AndTheConfirmedDatabase()
    {
        var csv = await ListToFileAsync(Target());

        await Assert.ThrowsAsync<ArgumentException>(() => ApplyAsync(csv, execute: true, op: ""));
        await Assert.ThrowsAsync<EnvironmentMismatchException>(() => new Backfill(Target(), _ => { }).ApplyAsync(new ApplyOptions
        {
            CsvPath = csv, Execute = false, ConfirmDatabase = "some-other-db",
        }));
        Assert.Null(await RowKind("C3"));
    }

    [Fact]
    public async Task APlanListedFromAnotherDatabase_IsRefused()
    {
        var csv = await ListToFileAsync(Target());
        await File.WriteAllTextAsync(csv, (await File.ReadAllTextAsync(csv)).Replace("," + DatabaseName + ",", ",prod-accumulators,"));

        await Assert.ThrowsAsync<EnvironmentMismatchException>(() => ApplyAsync(csv, execute: true));
        Assert.Null(await RowKind("C3"));
    }

    [Fact]
    public void CommandLine_IsStrict()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["apply", "--Execute"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["reconcile"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["apply", "--csv", "a", "--csv", "b"]));
        Assert.True(CommandLine.Parse(["apply", "--csv", "a", "--execute"]).Has("execute"));
    }

    private sealed class NullPublisher : IAccumulatorEventPublisher
    {
        public Task PublishAdjustedAsync(AccumulatorAdjustedEvent evt, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishOrphanAsync(OrphanAccumulatorClaimEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    }
}
