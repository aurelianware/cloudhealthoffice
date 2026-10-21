using System.Net;
using System.Text;
using MemberDocumentService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberDocumentService.Tests.Services;

/// <summary>What member-document-service actually sends document-service.</summary>
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

    private static CatalogUpload Upload(bool legalHold = false) => new(
        "tenant-1", "user-7", ["M-1", "M-2"], "IdCard", "IdCard/standard",
        new MemoryStream("%PDF-1.7"u8.ToArray()), "application/pdf", "doc-1.pdf", "Generated",
        "TX", new DateTime(2025, 12, 31), null, legalHold, legalHold ? "litigation 2026-17" : null,
        new Dictionary<string, string> { ["MemberDocumentId"] = "doc-1" });

    [Fact]
    public async Task Upload_FilesTheDocumentUnderEveryMember_ForTheUser()
    {
        var recorder = new Recorder(HttpStatusCode.Created, """{"id":"cat-1","sha256":"abc","sizeBytes":8}""");

        var result = await Catalog(recorder).UploadAsync(Upload());

        result.Should().Be(new CatalogDocumentInfo("cat-1", "abc", 8));
        var request = recorder.Request!;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.Should().Be("/api/v1/documents");
        request.Headers.GetValues("X-Tenant-ID").Should().Equal("tenant-1");
        request.Headers.GetValues(HttpDocumentCatalog.OnBehalfOfHeader).Should().Equal("user-7");
        recorder.RequestBody.Should().Contain("Member:M-1")
            .And.Contain("Member:M-2")
            .And.Contain("IdCard")
            .And.Contain("Generated")
            .And.Contain("MemberDocumentId=doc-1")
            .And.Contain("CoverageTerminationDate")
            .And.NotContain("LegalHold");
    }

    [Fact]
    public async Task Upload_UnderALegalHold_AsksTheCatalogToHoldItFromTheStart()
    {
        var recorder = new Recorder(HttpStatusCode.Created, """{"id":"cat-1"}""");

        await Catalog(recorder).UploadAsync(Upload(legalHold: true));

        recorder.RequestBody.Should().Contain("LegalHold").And.Contain("litigation 2026-17");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Upload_ThatTheCatalogRefuses_ThrowsWithItsStatus(HttpStatusCode status)
    {
        var act = () => Catalog(new Recorder(status, "{}")).UploadAsync(Upload());

        (await act.Should().ThrowAsync<DocumentCatalogException>()).Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task StartUpload_ReturnsTheCatalogsLink()
    {
        var recorder = new Recorder(HttpStatusCode.OK,
            """{"documentId":"cat-2","uploadUrl":"https://storage.example/x?sig=1","uploadMethod":"PUT","uploadHeaders":{"Content-Type":"application/pdf"},"expiresAtUtc":"2026-10-21T04:00:00Z"}""");

        var link = await Catalog(recorder).StartUploadAsync(new CatalogPresignedUpload(
            "tenant-1", "user-7", ["M-1"], "EOB", "EOB", "application/pdf", "doc-2.pdf", null, null, null, false, null));

        link.DocumentId.Should().Be("cat-2");
        link.UploadHeaders["Content-Type"].Should().Be("application/pdf");
        recorder.RequestBody.Should().Contain("Member:M-1");
    }

    [Fact]
    public async Task Finalize_AndHold_UseTheCatalogsRoutes()
    {
        var finalize = new Recorder(HttpStatusCode.OK, """{"id":"cat-existing"}""");
        var hold = new Recorder(HttpStatusCode.OK, """{"id":"cat-1","legalHold":true}""");

        var finished = await Catalog(finalize).FinalizeAsync("tenant-1", "user-7", "cat-2");
        var held = await Catalog(hold).SetLegalHoldAsync("tenant-1", "user-7", "cat-1", true, "subpoena");

        finished.DocumentId.Should().Be("cat-existing");
        finalize.Request!.RequestUri!.AbsolutePath.Should().Be("/api/v1/documents/cat-2/finalize");
        held.Should().BeTrue();
        hold.Request!.Method.Should().Be(HttpMethod.Put);
        hold.Request.RequestUri!.AbsolutePath.Should().Be("/api/v1/documents/cat-1/legal-hold");
        hold.RequestBody.Should().Contain("subpoena");
    }

    [Fact]
    public async Task Download_AsksForTheDocumentFromItsMember()
    {
        var recorder = new Recorder(HttpStatusCode.OK, "%PDF");

        var stream = await Catalog(recorder).DownloadAsync("tenant-1", "user-7", "cat-1", "M-1");

        stream.Should().NotBeNull();
        recorder.Request!.RequestUri!.PathAndQuery.Should().Be("/api/v1/documents/cat-1/content?subjectKind=Member&subjectId=M-1");
    }

    [Fact]
    public async Task Download_AndHold_OfADocumentTheCatalogDoesNotHave_AreNotFound()
    {
        (await Catalog(new Recorder(HttpStatusCode.NotFound, "")).DownloadAsync("t", "u", "cat-1", "M-1")).Should().BeNull();
        (await Catalog(new Recorder(HttpStatusCode.NotFound, "")).SetLegalHoldAsync("t", "u", "cat-1", true, null)).Should().BeFalse();
    }
}
