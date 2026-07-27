using System.Collections.Concurrent;
using System.Text;
using EligibilityService.Models;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// Exercises BatchJobStore through fake IBatchJobContainer /
/// IBatchBlobContainer adapters so the emulator isn't required in CI.
/// </summary>
public class BatchJobStoreTests
{
    [Fact]
    public async Task SmallPayload_StoresInline_AndReadsBack()
    {
        var (container, blobs) = Fakes();
        var store = new BatchJobStore(container, blobs, inlineMaxBytes: 1024);

        var job = new BatchEligibilityJob { TenantId = "t1", TotalRows = 3 };
        await store.SaveAsync(job);

        var payload = Encoding.UTF8.GetBytes("rowNumber,subscriberId\n2,SUB-1\n");
        await store.SaveResultAsync("t1", job.Id, payload);

        var read = await store.GetResultAsync("t1", job.Id);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
        // Blob never touched
        Assert.Empty(blobs.Uploaded);
    }

    [Fact]
    public async Task LargePayload_PromotesToBlob()
    {
        var (container, blobs) = Fakes();
        var store = new BatchJobStore(container, blobs, inlineMaxBytes: 64);

        var job = new BatchEligibilityJob { TenantId = "t1", TotalRows = 500 };
        await store.SaveAsync(job);

        var payload = Encoding.UTF8.GetBytes(new string('x', 1024));
        await store.SaveResultAsync("t1", job.Id, payload);

        Assert.Single(blobs.Uploaded);
        var read = await store.GetResultAsync("t1", job.Id);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task StreamingWrite_AlwaysGoesToBlob()
    {
        var (container, blobs) = Fakes();
        var store = new BatchJobStore(container, blobs, inlineMaxBytes: 1_048_576);

        var job = new BatchEligibilityJob { TenantId = "t1" };
        await store.SaveAsync(job);

        using var source = new MemoryStream(Encoding.UTF8.GetBytes("tiny"));
        await store.SaveResultStreamAsync("t1", job.Id, source);

        Assert.Single(blobs.Uploaded);
        await using var stream = await store.OpenResultStreamAsync("t1", job.Id);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Equal("tiny", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Partitioning_ByTenantId()
    {
        var (container, blobs) = Fakes();
        var store = new BatchJobStore(container, blobs);

        await store.SaveAsync(new BatchEligibilityJob { Id = "J1", TenantId = "tenant-a" });
        await store.SaveAsync(new BatchEligibilityJob { Id = "J1", TenantId = "tenant-b" });

        var a = await store.GetAsync("tenant-a", "J1");
        var b = await store.GetAsync("tenant-b", "J1");
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("tenant-a", a!.TenantId);
        Assert.Equal("tenant-b", b!.TenantId);

        // Read with wrong partition key returns null.
        Assert.Null(await store.GetAsync("other", "J1"));
    }

    [Fact]
    public async Task JobNotFound_ReturnsNull()
    {
        var (container, blobs) = Fakes();
        var store = new BatchJobStore(container, blobs);

        Assert.Null(await store.GetAsync("t1", "missing"));
        Assert.Null(await store.GetResultAsync("t1", "missing"));
    }

    private static (FakeJobContainer, FakeBlobContainer) Fakes()
        => (new FakeJobContainer(), new FakeBlobContainer());

    private class FakeJobContainer : IBatchJobContainer
    {
        private readonly ConcurrentDictionary<string, BatchEligibilityJob> _jobs = new();
        private readonly ConcurrentDictionary<string, (byte[]? inline, string? blobUri)> _payloads = new();

        private static string Key(string id, string pk) => $"{pk}::{id}";
        private static string PKey(string id, string pk, string k) => $"{pk}::{id}::{k}";

        public Task UpsertAsync(BatchEligibilityJob job, string partitionKey, CancellationToken ct)
        {
            _jobs[Key(job.Id, partitionKey)] = job;
            return Task.CompletedTask;
        }

        public Task<BatchEligibilityJob?> ReadAsync(string id, string partitionKey, CancellationToken ct)
        {
            _jobs.TryGetValue(Key(id, partitionKey), out var job);
            return Task.FromResult<BatchEligibilityJob?>(job);
        }

        public Task WriteInlinePayloadAsync(string id, string partitionKey, string payloadKey,
            byte[] bytes, CancellationToken ct)
        {
            _payloads[PKey(id, partitionKey, payloadKey)] = (bytes, null);
            return Task.CompletedTask;
        }

        public Task RecordBlobPayloadAsync(string id, string partitionKey, string payloadKey,
            string blobUri, CancellationToken ct)
        {
            _payloads[PKey(id, partitionKey, payloadKey)] = (null, blobUri);
            return Task.CompletedTask;
        }

        public Task<BatchPayloadRecord?> ReadPayloadAsync(string id, string partitionKey,
            string payloadKey, CancellationToken ct)
        {
            _payloads.TryGetValue(PKey(id, partitionKey, payloadKey), out var slot);
            if (slot.inline == null && slot.blobUri == null)
                return Task.FromResult<BatchPayloadRecord?>(null);
            return Task.FromResult<BatchPayloadRecord?>(
                new BatchPayloadRecord(slot.inline, slot.blobUri));
        }
    }

    private class FakeBlobContainer : IBatchBlobContainer
    {
        public ConcurrentDictionary<string, byte[]> Uploaded { get; } = new();

        public async Task<Uri> UploadAsync(string path, Stream content, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Uploaded[path] = ms.ToArray();
            return new Uri($"https://fake/{path}");
        }

        public Task DownloadToAsync(string path, Stream destination, CancellationToken ct)
        {
            if (!Uploaded.TryGetValue(path, out var bytes))
                throw new FileNotFoundException(path);
            return destination.WriteAsync(bytes, 0, bytes.Length, ct);
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
        {
            if (!Uploaded.TryGetValue(path, out var bytes))
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }
}
