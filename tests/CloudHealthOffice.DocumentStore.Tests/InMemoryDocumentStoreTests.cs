using CloudHealthOffice.DocumentStore;
using Xunit;

namespace CloudHealthOffice.DocumentStore.Tests;

public class InMemoryDocumentStoreTests
{
    private static InMemoryDocumentStore Make() => new();

    private static Stream TextStream(string content) =>
        new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));

    // ── Upload ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_StoresBlob_AndReturnsCorrectMetadata()
    {
        var store = Make();
        var result = await store.UploadAsync("docs", "tenant1/file.pdf", TextStream("hello"), "application/pdf");

        Assert.Equal("docs", result.Container);
        Assert.Equal("tenant1/file.pdf", result.BlobName);
        Assert.Equal(5, result.SizeBytes); // "hello" = 5 bytes
        Assert.Equal("memory://docs/tenant1/file.pdf", result.Uri.ToString());
    }

    [Fact]
    public async Task Upload_OverwritesExistingBlob()
    {
        var store = Make();
        await store.UploadAsync("docs", "file.txt", TextStream("v1"), "text/plain");
        await store.UploadAsync("docs", "file.txt", TextStream("version2"), "text/plain");

        var bytes = store.GetBytes("docs", "file.txt");
        Assert.Equal("version2", System.Text.Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public async Task Upload_IncrementsBlobCount()
    {
        var store = Make();
        Assert.Equal(0, store.Count);
        await store.UploadAsync("c", "a.pdf", TextStream("x"), "application/pdf");
        await store.UploadAsync("c", "b.pdf", TextStream("y"), "application/pdf");
        Assert.Equal(2, store.Count);
    }

    // ── Download ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Download_ReturnsCorrectContent()
    {
        var store = Make();
        await store.UploadAsync("attachments", "f.pdf", TextStream("pdfcontent"), "application/pdf");

        using var stream = await store.DownloadAsync("attachments", "f.pdf");
        using var reader = new StreamReader(stream);
        Assert.Equal("pdfcontent", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Download_Throws_WhenBlobNotFound()
    {
        var store = Make();
        await Assert.ThrowsAsync<DocumentNotFoundException>(
            () => store.DownloadAsync("container", "missing.pdf"));
    }

    [Fact]
    public async Task DocumentNotFoundException_CarriesContainerAndBlobName()
    {
        var store = Make();
        var ex = await Assert.ThrowsAsync<DocumentNotFoundException>(
            () => store.DownloadAsync("mycontainer", "path/to/file.pdf"));

        Assert.Equal("mycontainer", ex.Container);
        Assert.Equal("path/to/file.pdf", ex.BlobName);
    }

    // ── Exists ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exists_ReturnsFalse_WhenBlobNotPresent()
    {
        var store = Make();
        Assert.False(await store.ExistsAsync("c", "nope.pdf"));
    }

    [Fact]
    public async Task Exists_ReturnsTrue_AfterUpload()
    {
        var store = Make();
        await store.UploadAsync("c", "yes.pdf", TextStream("data"), "application/pdf");
        Assert.True(await store.ExistsAsync("c", "yes.pdf"));
    }

    // ── Delete ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesBlob()
    {
        var store = Make();
        await store.UploadAsync("c", "del.pdf", TextStream("data"), "application/pdf");
        Assert.True(await store.ExistsAsync("c", "del.pdf"));

        await store.DeleteAsync("c", "del.pdf");
        Assert.False(await store.ExistsAsync("c", "del.pdf"));
    }

    [Fact]
    public async Task Delete_IsNoOp_WhenBlobDoesNotExist()
    {
        var store = Make();
        // Should not throw
        await store.DeleteAsync("c", "nonexistent.pdf");
    }

    // ── GetUri ────────────────────────────────────────────────────────────

    [Fact]
    public void GetUri_ReturnsMemorySchemeUri()
    {
        var store = Make();
        var uri = store.GetUri("attachments", "tenant/claims/c1/att1.pdf");
        Assert.Equal("memory://attachments/tenant/claims/c1/att1.pdf", uri.ToString());
    }

    // ── Container isolation ───────────────────────────────────────────────

    [Fact]
    public async Task BlobsInDifferentContainers_AreIsolated()
    {
        var store = Make();
        await store.UploadAsync("container-a", "file.pdf", TextStream("aaa"), "application/pdf");
        await store.UploadAsync("container-b", "file.pdf", TextStream("bbb"), "application/pdf");

        Assert.True(await store.ExistsAsync("container-a", "file.pdf"));
        Assert.True(await store.ExistsAsync("container-b", "file.pdf"));

        var a = store.GetBytes("container-a", "file.pdf");
        var b = store.GetBytes("container-b", "file.pdf");
        Assert.Equal("aaa", System.Text.Encoding.UTF8.GetString(a!));
        Assert.Equal("bbb", System.Text.Encoding.UTF8.GetString(b!));
    }

    // ── Clear ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clear_RemovesAllBlobs()
    {
        var store = Make();
        await store.UploadAsync("c", "a.pdf", TextStream("x"), "application/pdf");
        await store.UploadAsync("c", "b.pdf", TextStream("y"), "application/pdf");
        store.Clear();
        Assert.Equal(0, store.Count);
    }
}
