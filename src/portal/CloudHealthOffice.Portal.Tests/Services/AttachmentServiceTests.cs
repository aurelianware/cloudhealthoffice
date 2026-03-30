using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class AttachmentServiceTests
{
    private readonly Mock<ILogger<AttachmentService>> _logger = new();
    private readonly Mock<ITokenAcquisition> _tokenAcquisition = new();
    private readonly IConfiguration _configuration;

    public AttachmentServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:AttachmentService"] = "http://localhost:5008"
            })
            .Build();

        _tokenAcquisition
            .Setup(t => t.GetAccessTokenForUserAsync(It.IsAny<IEnumerable<string>>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<System.Security.Claims.ClaimsPrincipal?>(),
                It.IsAny<TokenAcquisitionOptions?>()))
            .ReturnsAsync("fake-token");
    }

    private AttachmentService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new AttachmentService(httpClient, _configuration, _logger.Object, _tokenAcquisition.Object);
    }

    // ── GetAttachmentsAsync ──

    [Fact]
    public async Task GetAttachmentsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAttachmentsAsync("AUTH-001"));
        ex.ServiceName.Should().Be("Attachment Service");
    }

    // ── UploadAttachmentAsync ──

    [Fact]
    public async Task UploadAttachmentAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UploadAttachmentAsync("AUTH-001", stream, "doc.pdf", "application/pdf"));
        ex.ServiceName.Should().Be("Attachment Service");
    }

    // ── DownloadAttachmentAsync ──

    [Fact]
    public async Task DownloadAttachmentAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DownloadAttachmentAsync("AUTH-001", "ATT-001"));
        ex.ServiceName.Should().Be("Attachment Service");
    }

    // ── DeleteAttachmentAsync ──

    [Fact]
    public async Task DeleteAttachmentAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.DeleteAttachmentAsync("AUTH-001", "ATT-001"));
        ex.ServiceName.Should().Be("Attachment Service");
    }

    [Fact]
    public async Task GetAttachmentsAsync_ExceptionWrapsInnerException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.GetAttachmentsAsync("AUTH-001"));
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    // ════════════════════════════════════════════════════════════════
    // Happy-path tests
    // ════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GetAttachmentsAsync_WhenApiReturns200_DeserializesAttachmentList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { attachmentId = "ATT-1", fileName = "medical-records.pdf",
                  contentType = "application/pdf", fileSizeBytes = 245000L,
                  uploadedDate = "2025-03-01", uploadedBy = "admin@acme.com",
                  attachmentType = "MedicalRecord", blobPath = "/blobs/ATT-1" }
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.GetAttachmentsAsync("AUTH-001");

        result.Should().HaveCount(1);
        result[0].FileName.Should().Be("medical-records.pdf");
        result[0].ContentType.Should().Be("application/pdf");
        result[0].FileSizeBytes.Should().Be(245000);
        handler.CapturedUrls[0].Should().Contain("/attachments/authorization/AUTH-001");
    }

    [Fact]
    public async Task UploadAttachmentAsync_WhenApiReturns200_ExtractsAttachmentId()
    {
        var json = JsonSerializer.Serialize(new { attachmentId = "ATT-NEW-1" }, JsonOpts);
        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("PDF content"));
        var result = await sut.UploadAttachmentAsync("AUTH-001", stream, "doc.pdf", "application/pdf");

        result.Should().Be("ATT-NEW-1");
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/attachments/upload");
    }

    [Fact]
    public async Task DownloadAttachmentAsync_WhenApiReturns200_ReturnsStream()
    {
        var content = "file-content-bytes";
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent(content, Encoding.UTF8, "application/octet-stream");
            return response;
        });
        var sut = CreateService(new HttpClient(handler));

        var stream = await sut.DownloadAttachmentAsync("AUTH-001", "ATT-1");

        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync()).Should().Contain("file-content");
        handler.CapturedUrls[0].Should().Contain("/attachments/AUTH-001/ATT-1");
    }

    [Fact]
    public async Task DeleteAttachmentAsync_WhenApiReturns200_SendsDeleteToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "");
        var sut = CreateService(new HttpClient(handler));

        await sut.DeleteAttachmentAsync("AUTH-001", "ATT-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Delete);
        handler.CapturedUrls[0].Should().Contain("/attachments/AUTH-001/ATT-1");
    }

    [Fact]
    public async Task GetAttachmentsAsync_WhenApiReturnsNull_ReturnsEmptyList()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetAttachmentsAsync("AUTH-001");
        result.Should().BeEmpty();
    }
}
