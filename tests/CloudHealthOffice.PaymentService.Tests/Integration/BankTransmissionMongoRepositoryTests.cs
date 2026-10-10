using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using PaymentService.Models;
using PaymentService.Repositories;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests.Integration;

/// <summary>
/// The Mongo stores behind bank transmission, against a real mongod: the
/// transmission record's insert-if-absent and version-conditional replace, the
/// file ID modifier claims, and the run's EFT-file-only conditional write.
/// (The Cosmos implementations are covered by the Cosmos emulator CI work.)
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class BankTransmissionMongoRepositoryTests : IAsyncLifetime
{
    private const string Tenant = "tenant-m";
    private readonly MongoRunnerFixture _mongo;
    private readonly IMongoDatabase _db;

    public BankTransmissionMongoRepositoryTests(MongoRunnerFixture mongo)
    {
        _mongo = mongo;
        _db = mongo.CreateDatabase("payment_banktx");
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_db);

    private static PaymentFileTransmission Record(string reference = "FFS-PR-1", string tenant = Tenant) => new()
    {
        TenantId = tenant,
        PaymentRunId = "run-1",
        PaymentRunNumber = "PR-1",
        FileReference = reference,
        FileName = "ACH-FFS-PR-1.ach",
        ApprovedSha256 = new string('a', 64),
        ApprovedBy = "treasury-1",
        Status = PaymentFileTransmissionStatus.Pending,
    };

    [Fact]
    public async Task Transmission_insert_is_insert_if_absent()
    {
        var repo = new PaymentFileTransmissionRepositoryMongo(_db);

        Assert.True(await repo.TryInsertAsync(Record()));
        var duplicate = Record();
        duplicate.ApprovedBy = "someone-else";
        Assert.False(await repo.TryInsertAsync(duplicate));

        var stored = (await repo.GetAsync(Tenant, "FFS-PR-1"))!;
        Assert.Equal("treasury-1", stored.ApprovedBy);
        Assert.False(string.IsNullOrEmpty(stored.Version));
        Assert.Null(await repo.GetAsync("other-tenant", "FFS-PR-1"));
    }

    [Fact]
    public async Task Transmission_replace_applies_only_to_the_version_read()
    {
        var repo = new PaymentFileTransmissionRepositoryMongo(_db);
        await repo.TryInsertAsync(Record());
        var a = (await repo.GetAsync(Tenant, "FFS-PR-1"))!;
        var b = (await repo.GetAsync(Tenant, "FFS-PR-1"))!;

        a.Status = PaymentFileTransmissionStatus.Transmitting;
        a.AttemptCount = 1;
        Assert.True(await repo.TryReplaceAsync(a, a.Version));

        // The second claimant read the same version: refused, and keeps its version.
        var bVersion = b.Version;
        b.Status = PaymentFileTransmissionStatus.Transmitting;
        Assert.False(await repo.TryReplaceAsync(b, bVersion));
        Assert.Equal(bVersion, b.Version);

        var stored = (await repo.GetAsync(Tenant, "FFS-PR-1"))!;
        Assert.Equal((PaymentFileTransmissionStatus.Transmitting, 1, a.Version), (stored.Status, stored.AttemptCount, stored.Version));
        Assert.NotEqual(bVersion, stored.Version);
    }

    [Fact]
    public async Task Transmission_replace_never_crosses_tenants()
    {
        var repo = new PaymentFileTransmissionRepositoryMongo(_db);
        await repo.TryInsertAsync(Record());
        var mine = (await repo.GetAsync(Tenant, "FFS-PR-1"))!;
        var forged = Record(tenant: "other-tenant");
        forged.Id = mine.Id;

        Assert.False(await repo.TryReplaceAsync(forged, mine.Version));
    }

    [Fact]
    public async Task File_id_modifiers_are_claimed_once_per_day_and_kept_per_run()
    {
        var allocator = new NachaFileIdModifierAllocatorMongo(_db);
        var day = new DateTime(2026, 5, 4);

        var first = await allocator.AllocateAsync(Tenant, "091000019", "1123456789", day, "run-1");
        var second = await allocator.AllocateAsync(Tenant, "091000019", "1123456789", day, "run-2");
        var firstAgain = await allocator.AllocateAsync(Tenant, "091000019", "1123456789", day, "run-1");
        var nextDay = await allocator.AllocateAsync(Tenant, "091000019", "1123456789", day.AddDays(1), "run-3");

        Assert.Equal(("A", "B", "A", "A"), (first, second, firstAgain, nextDay));
    }

    [Fact]
    public async Task Concurrent_modifier_claims_never_share_a_modifier()
    {
        var allocator = new NachaFileIdModifierAllocatorMongo(_db);
        var day = new DateTime(2026, 5, 4);

        var got = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => allocator.AllocateAsync(Tenant, "091000019", "1123456789", day, "run-" + i))));

        Assert.Equal(8, got.Distinct().Count());
    }

    private PaymentRunRepositoryMongo Runs()
    {
        var context = new DefaultHttpContext();
        context.Items["TenantId"] = Tenant;
        return new PaymentRunRepositoryMongo(_db, new HttpContextAccessor { HttpContext = context },
            NullLogger<PaymentRunRepositoryMongo>.Instance);
    }

    [Fact]
    public async Task Eft_file_repin_applies_only_to_the_superseded_file_and_keeps_it_in_history()
    {
        var runs = Runs();
        await runs.CreateAsync(new PaymentRun { Id = "run-r", PaymentRunNumber = "PR-R", Status = PaymentRunStatus.Completed });
        var first = new PaymentRunEftFile { FileReference = "FFS-PR-R", Sha256 = new string('c', 64) };
        await runs.TrySaveEftFileAsync("run-r", first, null, Array.Empty<CheckFallbackPayment>(), Array.Empty<string>());

        first.SupersededByFileReference = "FFS-PR-R-R1";
        var second = new PaymentRunEftFile { FileReference = "FFS-PR-R-R1", Sha256 = new string('e', 64), Revision = 1 };
        Assert.True(await runs.TryRepinEftFileAsync("run-r", second, first, new[] { "835 date notice" }));
        // A second re-pin against the old file (a concurrent re-date) is refused.
        Assert.False(await runs.TryRepinEftFileAsync("run-r", new PaymentRunEftFile { Sha256 = new string('f', 64) }, first));

        var stored = (await runs.GetByIdAsync("run-r"))!;
        Assert.Equal(("FFS-PR-R-R1", 1), (stored.EftFile!.FileReference, stored.EftFile.Revision));
        Assert.Equal("FFS-PR-R-R1", Assert.Single(stored.EftFileHistory).SupersededByFileReference);
        Assert.Equal(new[] { "835 date notice" }, stored.Warnings);
    }

    [Fact]
    public async Task Eft_file_write_pins_once_and_touches_nothing_else()
    {
        var runs = Runs();
        await runs.CreateAsync(new PaymentRun { Id = "run-e", PaymentRunNumber = "PR-E", Status = PaymentRunStatus.Completed });
        var file = new PaymentRunEftFile { FileReference = "FFS-PR-E", Sha256 = new string('c', 64), GenerationCount = 1 };

        // Someone else writes the run in between (a finalize retry).
        var other = (await runs.GetByIdAsync("run-e"))!;
        other.PendingFinalizeClaimIds.Add("c9");
        await runs.UpdateAsync(other);

        Assert.True(await runs.TrySaveEftFileAsync("run-e", file, null, Array.Empty<CheckFallbackPayment>(), new[] { "w1" }));
        Assert.False(await runs.TrySaveEftFileAsync("run-e", new PaymentRunEftFile { Sha256 = new string('d', 64) }, null,
            Array.Empty<CheckFallbackPayment>(), Array.Empty<string>()));

        file.GenerationCount = 2;
        Assert.True(await runs.TrySaveEftFileAsync("run-e", file, new string('c', 64), Array.Empty<CheckFallbackPayment>(), Array.Empty<string>()));
        Assert.False(await runs.TrySaveEftFileAsync("run-e", file, new string('d', 64), Array.Empty<CheckFallbackPayment>(), Array.Empty<string>()));

        var stored = (await runs.GetByIdAsync("run-e"))!;
        Assert.Equal((new string('c', 64), 2), (stored.EftFile!.Sha256, stored.EftFile.GenerationCount));
        Assert.Contains("c9", stored.PendingFinalizeClaimIds);
        Assert.Contains("w1", stored.Warnings);
        Assert.Equal(PaymentRunStatus.Completed, stored.Status);
    }
}
