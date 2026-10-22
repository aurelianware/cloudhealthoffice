using System.Text;
using CloudHealthOffice.Testing.Mongo;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Runs;
using MongoDB.Driver;

namespace EnrollmentImportService.Tests.Repositories;

/// <summary>Import runs, unit records and GridFS raw files against a real mongod.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EnrollmentImportRunMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;
    private static readonly DateTime Now = new(2026, 10, 22, 15, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enrollment_runs");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static EnrollmentImportRun Queued(string id, string tenant = "t1", int minutesAgo = 5) => new()
    {
        Id = id, TenantId = tenant, BatchId = "B-" + id, Status = EnrollmentImportRunStatus.Queued,
        QueuedAt = Now.AddMinutes(-minutesAgo), StartedAt = Now.AddMinutes(-minutesAgo),
    };

    [Fact]
    public async Task Claim_TakesTheOldestQueuedRun_ThenOneWhoseLeaseRanOut_NeverOneStillLeased()
    {
        var runs = new EnrollmentImportRunRepository(_db);
        await runs.CreateAsync(Queued("new", minutesAgo: 1));
        await runs.CreateAsync(Queued("old", tenant: "t2", minutesAgo: 9));

        var first = await runs.TryClaimAsync("w1", Now, Now.AddMinutes(2));
        var second = await runs.TryClaimAsync("w2", Now, Now.AddMinutes(2));
        var none = await runs.TryClaimAsync("w3", Now.AddMinutes(1), Now.AddMinutes(3));
        var reclaimed = await runs.TryClaimAsync("w3", Now.AddMinutes(5), Now.AddMinutes(7));

        first!.Id.Should().Be("old");
        first.Status.Should().Be(EnrollmentImportRunStatus.Running);
        first.Attempts.Should().Be(1);
        second!.Id.Should().Be("new");
        none.Should().BeNull("both are leased");
        reclaimed!.LeaseOwner.Should().Be("w3");
        reclaimed.Attempts.Should().Be(2);
        (await runs.RenewLeaseAsync(reclaimed.TenantId, reclaimed.Id, "w1", Now.AddMinutes(9))).Should().BeFalse("w1 lost it");
    }

    [Fact]
    public async Task UnitResults_AddUp_AndTheRunCompletesOrFailsOnce()
    {
        var runs = new EnrollmentImportRunRepository(_db);
        await runs.CreateAsync(Queued("r1"));
        await runs.TryClaimAsync("w", Now, Now.AddMinutes(2));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => runs.AddUnitResultAsync("t1", "r1",
            new ImportResult { SuccessCount = 1, Errors = [$"e{i}"] }, failed: i % 5 == 0)));

        var run = (await runs.GetAsync("t1", "r1"))!;
        (run.CompletedUnits, run.FailedUnits, run.SuccessCount, run.Errors.Count).Should().Be((16, 4, 20, 20));
        (await runs.GetAsync("t2", "r1")).Should().BeNull();

        (await runs.TryCompleteAsync("t1", "r1", Now)).Should().BeTrue();
        (await runs.TryCompleteAsync("t1", "r1", Now)).Should().BeFalse();
        (await runs.TryFailAsync("t1", "r1", "Parse", "x", Now)).Should().BeFalse("a finished run stays finished");
    }

    [Fact]
    public async Task AUnitIsBegunOnce_AndInterruptedUnitsAreFailed()
    {
        var units = new EnrollmentImportUnitStore(_db);
        (await units.TryBeginAsync("t1", "r1", 0, "INS 1", Now)).Should().BeTrue();
        (await units.TryBeginAsync("t1", "r1", 0, "INS 1", Now)).Should().BeFalse();
        await units.TryBeginAsync("t1", "r1", 1, "INS 2", Now);
        await units.FinishAsync("t1", "r1", 0, false, null, Now);

        (await units.FailInterruptedAsync("t1", "r1", Now)).Should().Be(1);
        (await units.CountAsync("t1", "r1", EnrollmentImportUnitStatus.Done)).Should().Be(1);
        (await units.CountAsync("t1", "r1", EnrollmentImportUnitStatus.Failed)).Should().Be(1);
        (await units.CountAsync("t2", "r1", EnrollmentImportUnitStatus.Done)).Should().Be(0);
    }

    [Fact]
    public async Task GridFs_StoresAndStreamsTheRawFile_ForItsTenantOnly()
    {
        var store = new GridFsRawEdiFileStore(_db, TimeProvider.System);
        var content = new string('X', 600 * 1024); // several GridFS chunks
        var key = await store.SaveAsync("t1", "834", new MemoryStream(Encoding.UTF8.GetBytes(content)));

        await using (var read = await store.OpenAsync("t1", key))
        using (var reader = new StreamReader(read))
            (await reader.ReadToEndAsync()).Should().Be(content);

        var otherTenant = () => store.OpenAsync("t2", key);
        await otherTenant.Should().ThrowAsync<UnauthorizedAccessException>();
        var missing = () => store.OpenAsync("t1", "tenants/t1/edi/inbound/834/2026/10/22/none");
        await missing.Should().ThrowAsync<FileNotFoundException>();
    }
}
