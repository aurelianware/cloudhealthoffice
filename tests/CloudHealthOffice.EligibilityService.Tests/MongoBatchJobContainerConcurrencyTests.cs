using System.Reflection;
using System.Text;
using CloudHealthOffice.Testing.Mongo;
using EligibilityService.Models;
using EligibilityService.Services;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// Lost updates on the MongoDB batch-job document (EphemeralMongo). The job
/// and its payload slots share one document. Each write used to read the whole
/// document, change its own part and replace the whole document, so a write
/// made by another writer between that read and the replace was erased: a job
/// save dropped the result payload, a payload write reverted the job's status.
/// Each write now $sets only its own part. The tests run the other writer
/// exactly between the adapter's read and its write.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class MongoBatchJobContainerConcurrencyTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private const string Tenant = "tenant-batch";
    private const string BlobUri = "https://blobs.example.test/batch/result.csv";
    private IMongoDatabase _db = null!;
    private IMongoCollection<MongoContainerAdapter.JobDoc> _collection = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("elig_batch_concurrency");
        _collection = _db.GetCollection<MongoContainerAdapter.JobDoc>("BatchJobs");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private MongoContainerAdapter Plain() => new(_collection);

    /// <summary>An adapter whose first write is preceded by <paramref name="meanwhile"/>, after any read it made.</summary>
    private MongoContainerAdapter Interleaved(Func<Task> meanwhile)
        => new(InterleavingCollection<MongoContainerAdapter.JobDoc>.Wrap(_collection, meanwhile));

    private static BatchEligibilityJob Job(string id, BatchJobStatus status) => new()
    {
        Id = id,
        TenantId = Tenant,
        Status = status,
        TotalRows = 3,
    };

    private async Task<BatchEligibilityJob> SeedRunningAsync(string id)
    {
        var job = Job(id, BatchJobStatus.Running);
        await Plain().UpsertAsync(job, Tenant, default);
        return job;
    }

    [Fact]
    public async Task AJobSave_DoesNotEraseAResultPayloadRecordedSinceItsRead()
    {
        await SeedRunningAsync("J1");

        await Interleaved(() => Plain().RecordBlobPayloadAsync("J1", Tenant, "result", BlobUri, default))
            .UpsertAsync(Job("J1", BatchJobStatus.Completed), Tenant, default);

        var payload = await Plain().ReadPayloadAsync("J1", Tenant, "result", default);
        Assert.NotNull(payload);
        Assert.Equal(BlobUri, payload!.BlobUri);
        Assert.Equal(BatchJobStatus.Completed, (await Plain().ReadAsync("J1", Tenant, default))!.Status);
    }

    [Fact]
    public async Task AnInlinePayloadWrite_DoesNotRevertAJobSaveMadeSinceItsRead()
    {
        await SeedRunningAsync("J1");
        var bytes = Encoding.UTF8.GetBytes("rowNumber,subscriberId\n1,S1\n");

        await Interleaved(() => Plain().UpsertAsync(Job("J1", BatchJobStatus.Completed), Tenant, default))
            .WriteInlinePayloadAsync("J1", Tenant, "result", bytes, default);

        Assert.Equal(BatchJobStatus.Completed, (await Plain().ReadAsync("J1", Tenant, default))!.Status);
        Assert.Equal(bytes, (await Plain().ReadPayloadAsync("J1", Tenant, "result", default))!.Inline);
    }

    [Fact]
    public async Task ABlobPayloadRecord_DoesNotRevertAJobSaveMadeSinceItsRead()
    {
        await SeedRunningAsync("J1");

        await Interleaved(() => Plain().UpsertAsync(Job("J1", BatchJobStatus.Completed), Tenant, default))
            .RecordBlobPayloadAsync("J1", Tenant, "result", BlobUri, default);

        var job = (await Plain().ReadAsync("J1", Tenant, default))!;
        Assert.Equal(BatchJobStatus.Completed, job.Status);
        Assert.Equal(BatchStorageMode.Blob, job.StorageMode);
        Assert.Equal(BlobUri, job.ResultBlobUri);
        Assert.Equal(BlobUri, (await Plain().ReadPayloadAsync("J1", Tenant, "result", default))!.BlobUri);
    }

    [Fact]
    public async Task TwoPayloadSlotsOnOneDocument_DoNotEraseEachOther()
    {
        await SeedRunningAsync("J1");
        var input = Encoding.UTF8.GetBytes("input");
        var result = Encoding.UTF8.GetBytes("result");

        await Interleaved(() => Plain().WriteInlinePayloadAsync("J1", Tenant, "input", input, default))
            .WriteInlinePayloadAsync("J1", Tenant, "result", result, default);

        Assert.Equal(input, (await Plain().ReadPayloadAsync("J1", Tenant, "input", default))!.Inline);
        Assert.Equal(result, (await Plain().ReadPayloadAsync("J1", Tenant, "result", default))!.Inline);
    }

    [Fact]
    public async Task WritesToAMissingDocument_StillCreateIt_ScopedToTheTenant()
    {
        var bytes = Encoding.UTF8.GetBytes("input");

        await Plain().WriteInlinePayloadAsync("INPUT::J2", Tenant, "input", bytes, default);
        await Plain().UpsertAsync(Job("J3", BatchJobStatus.Queued), Tenant, default);

        Assert.Equal(bytes, (await Plain().ReadPayloadAsync("INPUT::J2", Tenant, "input", default))!.Inline);
        Assert.Null(await Plain().ReadPayloadAsync("INPUT::J2", "other-tenant", "input", default));
        Assert.Equal(BatchJobStatus.Queued, (await Plain().ReadAsync("J3", Tenant, default))!.Status);

        var raw = await _db.GetCollection<BsonDocument>("BatchJobs")
            .Find(new BsonDocument("_id", $"{Tenant}::INPUT::J2")).SingleAsync();
        Assert.Equal(Tenant, raw["TenantId"].AsString);
    }
}

/// <summary>
/// An <see cref="IMongoCollection{TDocument}"/> that runs a callback once,
/// just before the first write (replace or update) is sent, so another writer
/// lands between the caller's read and its write.
/// </summary>
public class InterleavingCollection<T> : DispatchProxy
{
    private static readonly HashSet<string> Writes = new(StringComparer.Ordinal)
    {
        nameof(IMongoCollection<T>.ReplaceOneAsync),
        nameof(IMongoCollection<T>.UpdateOneAsync),
        nameof(IMongoCollection<T>.FindOneAndReplaceAsync),
        nameof(IMongoCollection<T>.FindOneAndUpdateAsync),
    };

    private IMongoCollection<T> _target = null!;
    private Func<Task>? _meanwhile;

    public static IMongoCollection<T> Wrap(IMongoCollection<T> target, Func<Task> meanwhile)
    {
        var proxy = Create<IMongoCollection<T>, InterleavingCollection<T>>();
        var self = (InterleavingCollection<T>)(object)proxy;
        self._target = target;
        self._meanwhile = meanwhile;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod != null && Writes.Contains(targetMethod.Name) && _meanwhile is { } meanwhile)
        {
            _meanwhile = null;
            // Off the test's synchronization context, so blocking here cannot deadlock.
            Task.Run(meanwhile).GetAwaiter().GetResult();
        }

        try
        {
            return targetMethod!.Invoke(_target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
