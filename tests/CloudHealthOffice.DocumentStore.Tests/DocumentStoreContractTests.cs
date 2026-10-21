using Xunit;
using System.Net;
using System.Text;

namespace CloudHealthOffice.DocumentStore.Tests;

/// <summary>
/// The contract every <see cref="IDocumentStore"/> provider must meet, run
/// against each one: in memory always, and Azure Blob, Google Cloud Storage
/// and S3 against their emulators (Azurite, fake-gcs-server, MinIO) when the
/// emulator's environment variable is set. CI starts the emulators, so a
/// provider that behaves differently from the others fails the build instead
/// of a deployment.
///
/// Each test class gets its own container, so runs never see each other's
/// documents.
/// </summary>
public abstract class DocumentStoreContractTests : IAsyncLifetime
{
    protected IDocumentStore Store { get; private set; } = default!;
    protected string Container { get; } = $"contract-{Guid.NewGuid():N}"[..24];

    /// <summary>Create the store under test, or skip (Skip.If) when its emulator isn't configured.</summary>
    protected abstract Task<IDocumentStore> CreateStoreAsync();

    /// <summary>Whether this provider can sign upload links with the test credentials.</summary>
    protected abstract bool SignsUploadLinks { get; }

    /// <summary>Whether the backend under test keeps the content type given at upload (an emulator may not).</summary>
    protected virtual bool KeepsUploadContentType => true;

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    private async Task<string> ReadAsync(string name)
    {
        await using var stream = await Store.DownloadAsync(Container, name);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [SkippableFact]
    public async Task Upload_ThenDownload_RoundTripsTheBytes()
    {
        var result = await Store.UploadAsync(Container, "tenants/t1/a.txt", Bytes("hello document"), "text/plain");

        Assert.Equal(14, result.SizeBytes);
        Assert.Equal(Container, result.Container);
        Assert.Equal("tenants/t1/a.txt", result.BlobName);
        Assert.Equal("hello document", await ReadAsync("tenants/t1/a.txt"));
    }

    [SkippableFact]
    public async Task Upload_WithTags_RecordsSizeContentTypeAndTags()
    {
        var tags = new Dictionary<string, string>
        {
            ["retentionPolicyId"] = "DEFAULT-10Y",
            ["retentionUntilDate"] = "2036-10-20",
            ["legalHold"] = "false"
        };
        await Store.UploadAsync(Container, "tagged.pdf", Bytes("%PDF-1.7 body"), "application/pdf", tags);

        var props = await Store.GetPropertiesAsync(Container, "tagged.pdf");

        Assert.Equal(13, props.SizeBytes);
        if (KeepsUploadContentType)
            Assert.Equal("application/pdf", props.ContentType);
        Assert.Equal(tags.OrderBy(t => t.Key), props.Tags.OrderBy(t => t.Key));
    }

    [SkippableFact]
    public async Task SetTags_ReplacesTheTagsAndLeavesTheContent()
    {
        await Store.UploadAsync(Container, "retag.txt", Bytes("content"), "text/plain",
            new Dictionary<string, string> { ["legalHold"] = "false", ["stale"] = "x" });

        await Store.SetTagsAsync(Container, "retag.txt", new Dictionary<string, string> { ["legalHold"] = "true" });

        var props = await Store.GetPropertiesAsync(Container, "retag.txt");
        Assert.Equal(new Dictionary<string, string> { ["legalHold"] = "true" }, props.Tags);
        Assert.Equal("content", await ReadAsync("retag.txt"));
    }

    [SkippableFact]
    public async Task Upload_WithoutTags_HasNoTags()
    {
        await Store.UploadAsync(Container, "plain.txt", Bytes("x"), "text/plain");

        Assert.Empty((await Store.GetPropertiesAsync(Container, "plain.txt")).Tags);
    }

    [SkippableFact]
    public async Task Upload_FromANonSeekableStream_StoresAllBytes()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);

        var result = await Store.UploadAsync(Container, "stream.bin", new NonSeekableStream(payload), "application/octet-stream");

        Assert.Equal(payload.Length, result.SizeBytes);
        await using var stored = await Store.DownloadAsync(Container, "stream.bin");
        using var copy = new MemoryStream();
        await stored.CopyToAsync(copy);
        Assert.Equal(payload, copy.ToArray());
    }

    [SkippableFact]
    public async Task Upload_ToAnExistingName_ReplacesIt()
    {
        await Store.UploadAsync(Container, "replace.txt", Bytes("first"), "text/plain");
        await Store.UploadAsync(Container, "replace.txt", Bytes("second version"), "text/plain");

        Assert.Equal("second version", await ReadAsync("replace.txt"));
        Assert.Equal(14, (await Store.GetPropertiesAsync(Container, "replace.txt")).SizeBytes);
    }

    [SkippableFact]
    public async Task Containers_DoNotShareDocuments()
    {
        var other = $"other-{Guid.NewGuid():N}"[..24];
        await Store.UploadAsync(Container, "same/name.txt", Bytes("mine"), "text/plain");

        Assert.False(await Store.ExistsAsync(other, "same/name.txt"));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.DownloadAsync(other, "same/name.txt"));
    }

    [SkippableFact]
    public async Task Exists_AndDelete()
    {
        Assert.False(await Store.ExistsAsync(Container, "gone.txt"));
        await Store.UploadAsync(Container, "gone.txt", Bytes("x"), "text/plain");
        Assert.True(await Store.ExistsAsync(Container, "gone.txt"));

        await Store.DeleteAsync(Container, "gone.txt");

        Assert.False(await Store.ExistsAsync(Container, "gone.txt"));
        // Deleting what isn't there is not an error.
        await Store.DeleteAsync(Container, "gone.txt");
    }

    [SkippableFact]
    public async Task MissingDocuments_AreDocumentNotFound_NeverAProviderException()
    {
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.DownloadAsync(Container, "missing.txt"));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() => Store.GetPropertiesAsync(Container, "missing.txt"));
        await Assert.ThrowsAsync<DocumentNotFoundException>(() =>
            Store.SetTagsAsync(Container, "missing.txt", new Dictionary<string, string> { ["a"] = "b" }));
    }

    [SkippableFact]
    public async Task UploadLink_LetsAClientPutTheDocument_WithTheGivenHeaders()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(15);
        var link = await Store.CreateUploadLinkAsync(Container, "direct/upload.pdf.upload", "application/pdf", expires);

        if (!SignsUploadLinks)
        {
            Assert.Null(link);
            return;
        }

        Assert.NotNull(link);
        Assert.Equal("PUT", link!.Method);
        Assert.Equal("application/pdf", link.Headers["Content-Type"]);
        Assert.Equal(expires, link.ExpiresAtUtc);

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var put = new HttpRequestMessage(HttpMethod.Put, link.Url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("%PDF-1.7 direct"))
        };
        foreach (var (name, value) in link.Headers)
        {
            if (!put.Headers.TryAddWithoutValidation(name, value))
                put.Content.Headers.TryAddWithoutValidation(name, value);
        }
        if (put.Content.Headers.ContentType is null)
            put.Content.Headers.ContentType = new("application/pdf");

        using var response = await http.SendAsync(put);
        Assert.True(response.IsSuccessStatusCode, $"PUT returned {(int)response.StatusCode}");
        Assert.Equal("%PDF-1.7 direct", await ReadAsync("direct/upload.pdf.upload"));

        // The link only writes: it cannot be used to read the document back.
        using var get = await http.GetAsync(link.Url);
        Assert.False(get.IsSuccessStatusCode, $"GET with an upload link returned {(int)get.StatusCode}");
        Assert.NotEqual(HttpStatusCode.OK, get.StatusCode);
    }

    /// <summary>A stream that cannot seek or report its length, like a network body.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
