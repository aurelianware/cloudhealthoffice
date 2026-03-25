using System.Net;
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
}
