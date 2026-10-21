using System.Net;
using System.Text;
using AttachmentService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AttachmentService.Tests.Services;

/// <summary>What attachment-service actually sends document-service.</summary>
public class HttpDocumentCatalogTests
{
    private sealed class Recorder(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static HttpDocumentCatalog Catalog(Recorder recorder)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(HttpDocumentCatalog.ClientName, c => c.BaseAddress = new Uri("http://document-service/"))
            .ConfigurePrimaryHttpMessageHandler(() => recorder);
        var provider = services.BuildServiceProvider();
        return new HttpDocumentCatalog(provider.GetRequiredService<IHttpClientFactory>(), provider, NullLogger<HttpDocumentCatalog>.Instance);
    }

    [Fact]
    public async Task Upload_FilesTheDocumentOnTheParent_ForTheUser()
    {
        var recorder = new Recorder(HttpStatusCode.Created, """{"id":"doc-1"}""");

        var result = await Catalog(recorder).UploadAsync(new CatalogUpload(
            "tenant-1", "user-7", AttachmentParentType.Authorization, "AUTH-7", "Medical Records",
            new MemoryStream("%PDF-1.7"u8.ToArray()), "application/pdf", "a1.pdf", "X12-275",
            new Dictionary<string, string> { ["AttachmentId"] = "a1", ["RfaiReference"] = "R-1" }));

        result.DocumentId.Should().Be("doc-1");
        var request = recorder.Request!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/api/v1/documents");
        request.Headers.GetValues("X-Tenant-ID").Should().Equal("tenant-1");
        request.Headers.GetValues(HttpDocumentCatalog.OnBehalfOfHeader).Should().Equal("user-7");
        recorder.RequestBody.Should().Contain("Authorization:AUTH-7")
            .And.Contain("X12-275")
            .And.Contain("AttachmentId=a1")
            .And.Contain("RfaiReference=R-1");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Upload_ThatTheCatalogRefuses_Throws(HttpStatusCode status)
    {
        var act = () => Catalog(new Recorder(status, "{}")).UploadAsync(new CatalogUpload(
            "t", "u", AttachmentParentType.Claim, "C1", "x", new MemoryStream([1]), "application/pdf", "a.pdf", "Upload",
            new Dictionary<string, string>()));

        await act.Should().ThrowAsync<DocumentCatalogException>();
    }

    [Fact]
    public async Task Download_AsksForTheDocumentFromItsParent()
    {
        var recorder = new Recorder(HttpStatusCode.OK, "%PDF");

        var stream = await Catalog(recorder).DownloadAsync("tenant-1", "user-7", "doc-1", AttachmentParentType.Appeal, "A-9");

        stream.Should().NotBeNull();
        recorder.Request!.RequestUri!.PathAndQuery.Should().Be("/api/v1/documents/doc-1/content?subjectKind=Appeal&subjectId=A-9");
        recorder.Request.Headers.GetValues(HttpDocumentCatalog.OnBehalfOfHeader).Should().Equal("user-7");
    }

    [Fact]
    public async Task Download_ThatTheCatalogDoesNotHave_IsNull()
    {
        var stream = await Catalog(new Recorder(HttpStatusCode.NotFound, "")).DownloadAsync("t", "u", "doc-1", AttachmentParentType.Claim, "C1");
        stream.Should().BeNull();
    }

    [Fact]
    public async Task AddLink_WorksFromTheProvider_ToTheClaim_WithNoUserOnTheCall()
    {
        var recorder = new Recorder(HttpStatusCode.OK, """{"id":"doc-1"}""");

        var linked = await Catalog(recorder).AddLinkAsync("tenant-1", null, "doc-1",
            AttachmentParentType.Provider, "1234567893", AttachmentParentType.Claim, "CLM-9");

        linked.Should().BeTrue();
        recorder.Request!.Method.Should().Be(HttpMethod.Post);
        recorder.Request.RequestUri!.PathAndQuery.Should().Be("/api/v1/documents/doc-1/links?subjectKind=Provider&subjectId=1234567893");
        recorder.Request.Headers.Contains(HttpDocumentCatalog.OnBehalfOfHeader).Should().BeFalse();
        recorder.RequestBody.Should().Contain("Claim:CLM-9");
    }

    [Fact]
    public async Task AddLink_ToADocumentTheCatalogDoesNotHaveThere_IsFalse()
    {
        var linked = await Catalog(new Recorder(HttpStatusCode.NotFound, "")).AddLinkAsync("t", "u", "doc-1",
            AttachmentParentType.Provider, "P1", AttachmentParentType.Claim, "C1");
        linked.Should().BeFalse();
    }
}
