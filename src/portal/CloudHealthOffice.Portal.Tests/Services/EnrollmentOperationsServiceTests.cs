using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class EnrollmentOperationsServiceTests
{
    private readonly Mock<ILogger<EnrollmentOperationsService>> _logger = new();
    private readonly IConfiguration _configuration;

    public EnrollmentOperationsServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:MemberService"] = "http://localhost:5001"
            })
            .Build();
    }

    private EnrollmentOperationsService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new EnrollmentOperationsService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetTodaySummaryAsync_WhenApiFails_ReturnsMockSummary()
    {
        var sut = CreateService();
        var result = await sut.GetTodaySummaryAsync();

        result.Should().NotBeNull();
        result.FilesReceived.Should().BeGreaterThan(0);
        result.TotalTransactions.Should().BeGreaterThan(0);
        result.MembersAdded.Should().BeGreaterThan(0);
        result.MembersTermed.Should().BeGreaterThan(0);
        result.ErrorCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetRecentFilesAsync_WhenApiFails_ReturnsMockFiles()
    {
        var sut = CreateService();
        var result = await sut.GetRecentFilesAsync();

        result.Should().NotBeEmpty();
        result.Count.Should().BeGreaterOrEqualTo(5);
    }

    [Fact]
    public async Task GetRecentFilesAsync_MockFiles_HaveRequiredFields()
    {
        var sut = CreateService();
        var files = await sut.GetRecentFilesAsync();

        foreach (var file in files)
        {
            file.FileId.Should().NotBeNullOrEmpty();
            file.FileName.Should().NotBeNullOrEmpty();
            file.FileName.Should().EndWith(".edi");
            file.SponsorName.Should().NotBeNullOrEmpty();
            file.GroupNumber.Should().NotBeNullOrEmpty();
            file.Status.Should().BeOneOf("Completed", "Processing", "Failed", "Partial");
        }
    }

    [Fact]
    public async Task GetRecentFilesAsync_MockFiles_IncludeVariousStatuses()
    {
        var sut = CreateService();
        var files = await sut.GetRecentFilesAsync();
        var statuses = files.Select(f => f.Status).Distinct().ToList();

        statuses.Should().Contain("Completed");
        statuses.Should().Contain("Processing");
        statuses.Should().Contain("Failed");
    }

    [Fact]
    public async Task GetFileDetailAsync_WhenApiFails_ReturnsMockDetail()
    {
        var sut = CreateService();
        var files = await sut.GetRecentFilesAsync();
        var detail = await sut.GetFileDetailAsync(files[0].FileId);

        detail.Should().NotBeNull();
        detail.FileId.Should().Be(files[0].FileId);
        detail.FileName.Should().Be(files[0].FileName);
    }

    [Fact]
    public async Task GetFileDetailAsync_FileWithRejections_HasRejectionDetails()
    {
        var sut = CreateService();
        var files = await sut.GetRecentFilesAsync();
        var fileWithRejections = files.First(f => f.RejectedCount > 0);
        var detail = await sut.GetFileDetailAsync(fileWithRejections.FileId);

        detail.Rejections.Should().NotBeEmpty();
        foreach (var rej in detail.Rejections)
        {
            rej.MemberId.Should().NotBeNullOrEmpty();
            rej.ErrorCode.Should().StartWith("834-E");
            rej.ErrorDescription.Should().NotBeNullOrEmpty();
            rej.RawSegmentReference.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task GetRecentFilesAsync_TransactionCountsAreConsistent()
    {
        var sut = CreateService();
        var files = await sut.GetRecentFilesAsync();

        foreach (var file in files.Where(f => f.Status != "Failed"))
        {
            var sumParts = file.AddedCount + file.TermedCount + file.ChangedCount + file.RejectedCount;
            file.TransactionCount.Should().Be(sumParts,
                $"File {file.FileId}: {file.AddedCount}+{file.TermedCount}+{file.ChangedCount}+{file.RejectedCount} should equal {file.TransactionCount}");
        }
    }
}
