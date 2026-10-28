using CloudHealthOffice.Testing.Mongo;
using FhirService.Models;
using FhirService.Services.BulkExport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using MongoDB.Driver;

namespace CloudHealthOffice.FhirService.Tests.Services.BulkExport;

/// <summary>The bulk export job store on MongoDB (EphemeralMongo).</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoBulkExportJobStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;
    private MongoBulkExportJobStore _store = null!;

    public MongoBulkExportJobStoreTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("bulk_export_jobs");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:DatabaseName"] = _database.DatabaseNamespace.DatabaseName,
            })
            .Build();
        _store = new MongoBulkExportJobStore(_mongo.Client, configuration);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private static BulkExportJobRecord Job(string tenant, string id, DateTimeOffset createdAt) => new()
    {
        TenantId = tenant,
        JobId = id,
        RequestedBy = "prov-1",
        GroupId = "prov-1",
        Types = ["Patient", "Coverage"],
        Since = T0.AddDays(-30),
        RequestUrl = "https://fhir.test/fhir/r4/Group/prov-1/$export",
        CreatedAt = createdAt,
    };

    [Fact]
    public async Task Job_RoundTrips_AndIsNeverFoundFromAnotherTenant()
    {
        await _store.CreateAsync(Job("t1", "j1", T0));

        var job = await _store.GetAsync("t1", "j1");
        job!.Status.Should().Be(BulkExportStatus.Accepted);
        job.Types.Should().Equal("Patient", "Coverage");
        job.Since.Should().Be(T0.AddDays(-30));
        job.CreatedAt.Should().Be(T0);
        job.Level.Should().Be(BulkExportLevel.Group);
        job.GroupId.Should().Be("prov-1");
        (await _store.GetAsync("t2", "j1")).Should().BeNull();
    }

    [Fact]
    public async Task APatientExport_RoundTripsWithNoGroup()
    {
        await _store.CreateAsync(Job("t1", "p1", T0) with { Level = BulkExportLevel.Patient, GroupId = null });

        var job = await _store.GetAsync("t1", "p1");
        job!.Level.Should().Be(BulkExportLevel.Patient);
        job.GroupId.Should().BeNull();
    }

    [Fact]
    public async Task Claim_TakesTheOldestWaitingJob_AndReclaimsOnlyAnExpiredLease()
    {
        await _store.CreateAsync(Job("t1", "newer", T0.AddMinutes(1)));
        await _store.CreateAsync(Job("t2", "older", T0));

        (await _store.ClaimNextAsync(T0, T0.AddMinutes(10)))!.JobId.Should().Be("older");
        (await _store.ClaimNextAsync(T0, T0.AddMinutes(10)))!.JobId.Should().Be("newer");
        (await _store.ClaimNextAsync(T0.AddMinutes(5), T0.AddMinutes(15))).Should().BeNull("both leases are live");

        var reclaimed = await _store.ClaimNextAsync(T0.AddMinutes(11), T0.AddMinutes(21));
        reclaimed!.JobId.Should().Be("older");
        reclaimed.Status.Should().Be(BulkExportStatus.InProgress);
    }

    [Fact]
    public async Task Complete_RecordsTheFiles_OnlyWhileRunning()
    {
        await _store.CreateAsync(Job("t1", "j1", T0));
        List<BulkExportStoredFile> files = [new("Patient", "Patient-1.ndjson", 3, "tenants/t1/j1/Patient-1.ndjson")];

        (await _store.CompleteAsync("t1", "j1", files, T0, T0, T0.AddDays(1), T0.AddDays(2)))
            .Should().BeFalse("a job that was never claimed is not running");

        await _store.ClaimNextAsync(T0, T0.AddMinutes(10));
        (await _store.ReportProgressAsync("t1", "j1", 50, T0.AddMinutes(20))).Should().BeTrue();
        (await _store.CompleteAsync("t1", "j1", files, T0, T0.AddMinutes(1), T0.AddDays(1), T0.AddDays(2)))
            .Should().BeTrue();

        var job = await _store.GetAsync("t1", "j1");
        job!.Status.Should().Be(BulkExportStatus.Complete);
        job.ProgressPercent.Should().Be(100);
        job.LeaseUntil.Should().BeNull();
        job.ExpiresAt.Should().Be(T0.AddDays(1));
        job.TransactionTime.Should().Be(T0);
        job.Files.Should().Equal(files);
    }

    [Fact]
    public async Task Cancel_StopsARunningJob_AndMakesItPurgeableAtOnce()
    {
        await _store.CreateAsync(Job("t1", "j1", T0));
        await _store.ClaimNextAsync(T0, T0.AddMinutes(10));

        (await _store.CancelAsync("t1", "j1", T0.AddMinutes(1))).Should().BeTrue();
        (await _store.CancelAsync("t1", "j1", T0.AddMinutes(2))).Should().BeFalse("already cancelled");
        (await _store.ReportProgressAsync("t1", "j1", 10, T0.AddMinutes(20))).Should().BeFalse();
        (await _store.CompleteAsync("t1", "j1", [], T0, T0, T0, T0)).Should().BeFalse();

        var due = await _store.ListPurgeableAsync(T0.AddMinutes(1), 10);
        due.Select(j => j.JobId).Should().Equal("j1");

        await _store.MarkPurgedAsync("t1", "j1");
        (await _store.ListPurgeableAsync(T0.AddDays(5), 10)).Should().BeEmpty();
        (await _store.GetAsync("t1", "j1"))!.Purged.Should().BeTrue();
    }

    [Fact]
    public async Task Fail_RecordsTheError_AndWhenToPurge()
    {
        await _store.CreateAsync(Job("t1", "j1", T0));
        await _store.ClaimNextAsync(T0, T0.AddMinutes(10));

        await _store.FailAsync("t1", "j1", "The export could not be completed.", T0.AddMinutes(1), T0.AddDays(2));

        var job = await _store.GetAsync("t1", "j1");
        job!.Status.Should().Be(BulkExportStatus.Error);
        job.Error.Should().Be("The export could not be completed.");
        (await _store.ListPurgeableAsync(T0.AddDays(1), 10)).Should().BeEmpty();
        (await _store.ListPurgeableAsync(T0.AddDays(2), 10)).Should().ContainSingle();
    }
}
