using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Data;
using EphemeralMongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace AuthorizationService.Tests.Persistence;

public sealed class PaReportingPackageMongoTests : IDisposable
{
    private readonly IMongoRunner _runner;
    private readonly MongoClient _client;
    private readonly IMongoDatabase _db;
    private static readonly DateTime Receipt = new(2025, 1, 2, 12, 0, 0, DateTimeKind.Utc);
    public PaReportingPackageMongoTests()
    {
        _runner = MongoRunner.Run(new MongoRunnerOptions
        { Version = MongoVersion.V7, Edition = MongoEdition.Community, UseSingleNodeReplicaSet = true });
        _client = new MongoClient(_runner.ConnectionString);
        _db = _client.GetDatabase("pa_packages_" + Guid.NewGuid().ToString("N"));
    }
    public void Dispose() { _client.Dispose(); _runner.Dispose(); }
    private PaReportingPackageRepositoryMongo Repo(string tenant = "tenant", PaReportingPackageOptions? limits = null, IMongoDatabase? db = null)
    {
        var context = new DefaultHttpContext(); context.Items["TenantId"] = tenant;
        return new(db ?? _db, new HttpContextAccessor { HttpContext = context }, Options.Create(limits ?? new()));
    }
    private static FreezePaReportingPackage Command(string key = "freeze-1") => new()
    {
        RequestKey = key, PlanId = "plan-1", PlanScopeReference = "mapping:review-1", Year = 2025,
        TimeZone = "America/Los_Angeles", LineOfBusiness = LineOfBusiness.Medicaid,
        OutcomeCutoff = Receipt.AddMonths(1), KnowledgeCutoff = Receipt.AddMonths(1),
    };
    private static Authorization Row(string id, string tenant = "tenant") => new()
    {
        Id = id, TenantId = tenant, MemberId = "DO-NOT-RETAIN-MEMBER-DETAILS", Revision = 3,
        LineOfBusiness = LineOfBusiness.Medicaid, SubmittedDate = Receipt,
        ReportingLedger = new()
        {
            ReceivedAt = Receipt, RecordedAt = Receipt, LineOfBusiness = LineOfBusiness.Medicaid,
            Facts = new() { new()
            {
                EventId = "native-decision:3", SourceSystem = "cho-native", EvidenceReference = "revision:3",
                Kind = PaReportingFactKind.Determination, Outcome = AuthorizationStatus.Denied,
                EffectiveAt = Receipt.AddDays(1), RecordedAt = Receipt.AddDays(1), DecisionAt = Receipt.AddDays(1),
            } },
        },
    };
    private Task Seed(params Authorization[] rows) => _db.GetCollection<Authorization>("Authorizations").InsertManyAsync(rows);

    [Fact]
    public async Task RetainedInputsReproduceMetricsAndHashes_WithoutRetainingFullClinicalRecord()
    {
        await Seed(Row("b"), Row("a"));
        var package = await Repo().FreezeAsync(Command(), "reviewer");
        var persisted = (await Repo().GetAsync(package.Id))!;
        persisted.PackageHash.Should().Be(package.PackageHash);
        persisted.ReportJson.Should().Be(package.ReportJson);
        persisted.FrozenReport.GetProperty("AcceptanceGaps").GetArrayLength().Should().BeGreaterThan(0);
        persisted.PublicationReady.Should().BeFalse(); persisted.PlanMappingVerified.Should().BeFalse();
        persisted.Report.Standard.Denied.Should().Be(2);
        var inputs = (await Repo().ReadInputsAsync(package.Id, -1, 100))!;
        inputs.Select(i => i.Input.AuthorizationId).Should().Equal("a", "b");
        PaReportingPackageHash.Inputs(inputs).Should().Be(package.InputsHash);
        var calculator = new global::AuthorizationService.Services.Cms0057.AnnualPaReporting(2025, "America/Los_Angeles",
            LineOfBusiness.Medicaid, package.Scope.OutcomeCutoffUtc, package.Scope.KnowledgeCutoffUtc);
        foreach (var i in inputs) calculator.Add(new Authorization { ReportingLedger = i.Input.Ledger });
        PaReportingPackageHash.Of(calculator.Finish()).Should().Be(PaReportingPackageHash.Of(package.Report));
        System.Text.Encoding.UTF8.GetString(PaReportingPackageHash.Bytes(inputs)).Should().NotContain("DO-NOT-RETAIN-MEMBER-DETAILS");
    }

    [Fact]
    public async Task PrivateInputPagesHaveStableOrdinals_AndInvalidOffsetsAreRejected()
    {
        await Seed(Row("a"), Row("b"));
        var package = await Repo().FreezeAsync(Command(), "actor");
        (await Repo().ReadInputsAsync(package.Id, -1, 1))!.Single().Ordinal.Should().Be(0);
        (await Repo().ReadInputsAsync(package.Id, 0, 1))!.Single().Ordinal.Should().Be(1);
        (await Repo().ReadInputsAsync(package.Id, 1, 1))!.Should().BeEmpty();
        await ((Func<Task>)(() => Repo().ReadInputsAsync(package.Id, 2, 1))).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task LaterAppealAndSourceDeletion_CannotRewriteFrozenPackage_AndRestatementGetsNewIdentity()
    {
        await Seed(Row("a"));
        var original = await Repo().FreezeAsync(Command(), "original-actor");
        var row = Row("a"); row.ReportingLedger!.Facts.Add(new()
        {
            EventId = "appeal-1", SourceSystem = "appeals", Kind = PaReportingFactKind.AppealDecision,
            Outcome = AuthorizationStatus.Approved, EffectiveAt = Receipt.AddDays(3), RecordedAt = Receipt.AddDays(4),
        });
        await _db.GetCollection<Authorization>("Authorizations").ReplaceOneAsync(a => a.Id == "a", row);
        var restatement = await Repo().FreezeAsync(Command("freeze-2"), "other-actor");
        restatement.Report.Standard.ApprovedAfterAppeal.Should().Be(1);
        restatement.InputsHash.Should().NotBe(original.InputsHash);
        await _db.GetCollection<Authorization>("Authorizations").DeleteManyAsync(Builders<Authorization>.Filter.Empty);
        var replay = await Repo().FreezeAsync(Command(), "retry-actor");
        replay.PackageHash.Should().Be(original.PackageHash); replay.CreatedBy.Should().Be("original-actor");
        replay.Report.Standard.Denied.Should().Be(1);
        (await Repo().ReadInputsAsync(original.Id, -1, 100))!.Should().ContainSingle();
    }

    [Fact]
    public async Task ChangedScopeUnderSameKey_Conflicts_AndConcurrentExactRequestsReturnOnePackage()
    {
        await Seed(Row("a"));
        var results = await Task.WhenAll(Repo().FreezeAsync(Command(), "one"), Repo().FreezeAsync(Command(), "two"));
        results[0].PackageHash.Should().Be(results[1].PackageHash);
        var changed = Command(); changed.PlanId = "different-plan";
        await ((Func<Task>)(() => Repo().FreezeAsync(changed, "three"))).Should().ThrowAsync<ConcurrencyConflictException>();
        (await _db.GetCollection<PaReportingPackage>("PaReportingPackages").CountDocumentsAsync(Builders<PaReportingPackage>.Filter.Empty)).Should().Be(1);
    }

    [Fact]
    public async Task OtherTenantCannotReadPackageOrInputs_AndSameKeyDoesNotCollide()
    {
        await Seed(Row("a"), Row("b", "other"));
        var one = await Repo().FreezeAsync(Command(), "one");
        (await Repo("other").GetAsync(one.Id)).Should().BeNull();
        (await Repo("other").ReadInputsAsync(one.Id, -1, 100)).Should().BeNull();
        var two = await Repo("other").FreezeAsync(Command(), "two");
        two.Id.Should().NotBe(one.Id); two.Report.Standard.Requests.Should().Be(1);
    }

    [Fact]
    public async Task RecordLimitAfterFirstBatch_AbortsInputsAndHeader_WithoutTruncating()
    {
        await Seed(Enumerable.Range(0, 251).Select(n => Row("a" + n.ToString("D4"))).ToArray());
        await ((Func<Task>)(() => Repo(limits: new() { MaxRecords = 250 }).FreezeAsync(Command(), "actor")))
            .Should().ThrowAsync<PaReportingPackageLimitException>();
        (await _db.GetCollection<PaReportingPackageInput>("PaReportingPackageInputs").CountDocumentsAsync(Builders<PaReportingPackageInput>.Filter.Empty)).Should().Be(0);
        (await _db.GetCollection<PaReportingPackage>("PaReportingPackages").CountDocumentsAsync(Builders<PaReportingPackage>.Filter.Empty)).Should().Be(0);
    }

    [Fact]
    public async Task ByteLimit_AbortsWithoutACompletedPackage()
    {
        await Seed(Row("a"));
        await ((Func<Task>)(() => Repo(limits: new() { MaxInputBytes = 1 }).FreezeAsync(Command(), "actor")))
            .Should().ThrowAsync<PaReportingPackageLimitException>();
        (await _db.GetCollection<PaReportingPackage>("PaReportingPackages").CountDocumentsAsync(Builders<PaReportingPackage>.Filter.Empty)).Should().Be(0);
    }

    [Fact]
    public async Task TamperedHeader_IsNotReturnedAsValidEvidence()
    {
        await Seed(Row("a")); var package = await Repo().FreezeAsync(Command(), "actor");
        await _db.GetCollection<PaReportingPackage>("PaReportingPackages").UpdateOneAsync(p => p.Id == package.Id,
            Builders<PaReportingPackage>.Update.Set(p => p.Scope.PlanId, "tampered"));
        await ((Func<Task>)(() => Repo().GetAsync(package.Id))).Should().ThrowAsync<PaReportingPackageIntegrityException>();
    }

    [Fact]
    public async Task TamperedInputOrMissingOrdinal_IsDetectedOnPrivateRetrieval()
    {
        await Seed(Row("a"), Row("b")); var package = await Repo().FreezeAsync(Command(), "actor");
        var store = _db.GetCollection<PaReportingPackageInput>("PaReportingPackageInputs");
        await store.UpdateOneAsync(i => i.PackageId == package.Id && i.Ordinal == 0,
            Builders<PaReportingPackageInput>.Update.Set(i => i.Input.Revision, 999));
        await ((Func<Task>)(() => Repo().ReadInputsAsync(package.Id, -1, 100))).Should().ThrowAsync<PaReportingPackageIntegrityException>();
        await store.DeleteOneAsync(i => i.PackageId == package.Id && i.Ordinal == 0);
        await ((Func<Task>)(() => Repo().ReadInputsAsync(package.Id, -1, 100))).Should().ThrowAsync<PaReportingPackageIntegrityException>();
    }

    [Fact]
    public async Task LegacyInputsRemainDiagnostic_AndEmptyCohortRemainsUnapproved()
    {
        var row = Row("legacy"); row.ReportingLedger = null;
        await Seed(row);
        var package = await Repo().FreezeAsync(Command(), "actor");
        package.Report.LegacyRecordsWithoutEvidence.Should().Be(1);
        package.Report.Standard.Requests.Should().Be(0);
        var empty = await Repo("empty").FreezeAsync(Command(), "actor");
        empty.InputCount.Should().Be(0); empty.PublicationReady.Should().BeFalse();
        empty.Report.Standard.ApprovedPercent.Should().BeNull();
    }

    [Fact]
    public async Task UnsupportedStandaloneMongo_CannotProduceAPackage()
    {
        using var runner = MongoRunner.Run(new MongoRunnerOptions { Version = MongoVersion.V7, Edition = MongoEdition.Community });
        using var client = new MongoClient(runner.ConnectionString);
        var db = client.GetDatabase("packages_standalone");
        await ((Func<Task>)(() => Repo(db: db).FreezeAsync(Command(), "actor"))).Should().ThrowAsync<PaReportingPackageUnavailableException>();
        (await db.GetCollection<PaReportingPackage>("PaReportingPackages").CountDocumentsAsync(Builders<PaReportingPackage>.Filter.Empty)).Should().Be(0);
    }
}
