using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CorrespondenceServiceTests
{
    private readonly Mock<ILogger<CorrespondenceService>> _logger = new();
    private readonly IConfiguration _configuration;

    public CorrespondenceServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ClaimsService"] = "http://localhost:5000"
            })
            .Build();
    }

    private CorrespondenceService CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new CorrespondenceService(httpClient, _configuration, _logger.Object);
    }

    [Fact]
    public async Task GetSummaryAsync_WhenApiFails_ReturnsMockSummary()
    {
        var sut = CreateService();
        var result = await sut.GetSummaryAsync();

        result.Should().NotBeNull();
        result.PendingGeneration.Should().BeGreaterThan(0);
        result.GeneratedToday.Should().BeGreaterThan(0);
        result.SentThisWeek.Should().BeGreaterThan(0);
        result.FailedReturned.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetQueueAsync_NoFilters_ReturnsAllMockItems()
    {
        var sut = CreateService();
        var result = await sut.GetQueueAsync();

        result.Should().NotBeEmpty();
        result.Count.Should().BeGreaterOrEqualTo(25);
    }

    [Fact]
    public async Task GetQueueAsync_MockItems_HaveRequiredFields()
    {
        var sut = CreateService();
        var items = await sut.GetQueueAsync();

        foreach (var item in items)
        {
            item.LetterId.Should().StartWith("LTR-");
            item.LetterType.Should().BeOneOf(
                "Adverse Determination", "EOB", "RFAI", "Welcome Letter", "Payment Notice");
            item.RecipientName.Should().NotBeNullOrEmpty();
            item.RecipientType.Should().BeOneOf("Member", "Provider");
            item.RelatedId.Should().NotBeNullOrEmpty();
            item.Status.Should().BeOneOf(
                "Queued", "Generated", "Sent", "Delivered", "Failed", "Returned");
            item.DeliveryMethod.Should().BeOneOf("Mail", "Fax", "Portal", "Email");
        }
    }

    [Theory]
    [InlineData("Adverse Determination")]
    [InlineData("EOB")]
    [InlineData("RFAI")]
    [InlineData("Welcome Letter")]
    [InlineData("Payment Notice")]
    public async Task GetQueueAsync_FilterByType_ReturnsOnlyMatchingItems(string type)
    {
        var sut = CreateService();
        var items = await sut.GetQueueAsync(type: type);

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.LetterType == type);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Sent")]
    public async Task GetQueueAsync_FilterByStatus_ReturnsOnlyMatchingItems(string status)
    {
        var sut = CreateService();
        var items = await sut.GetQueueAsync(status: status);

        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.Status == status);
    }

    [Fact]
    public async Task GetQueueAsync_QueuedItems_HaveNoGeneratedDate()
    {
        var sut = CreateService();
        var items = await sut.GetQueueAsync(status: "Queued");

        items.Where(i => i.GeneratedDate == null).Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_WhenApiFails_ReturnsMockRfais()
    {
        var sut = CreateService();
        var result = await sut.GetOutstandingRfaisAsync();

        result.Should().NotBeEmpty();
        result.Count.Should().Be(5);
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_MockRfais_HaveRequiredFields()
    {
        var sut = CreateService();
        var rfais = await sut.GetOutstandingRfaisAsync();

        foreach (var rfai in rfais)
        {
            rfai.RfaiId.Should().StartWith("RFAI-");
            rfai.RecipientName.Should().NotBeNullOrEmpty();
            rfai.RecipientType.Should().Be("Provider");
            rfai.RelatedClaimId.Should().StartWith("CLM-");
            rfai.DocumentsRequested.Should().NotBeNullOrEmpty();
            rfai.DaysSinceSent.Should().BeGreaterThan(0);
            rfai.DaysUntilDeadline.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_MockRfais_HaveVaryingAges()
    {
        var sut = CreateService();
        var rfais = await sut.GetOutstandingRfaisAsync();
        var ages = rfais.Select(r => r.DaysSinceSent).ToList();

        // Should have a spread of ages (3, 12, 28, 35, 41 days per spec)
        ages.Min().Should().BeLessThan(10);
        ages.Max().Should().BeGreaterThan(30);
    }

    [Fact]
    public async Task GetOutstandingRfaisAsync_IncludesApproachingDeadline()
    {
        var sut = CreateService();
        var rfais = await sut.GetOutstandingRfaisAsync();

        rfais.Should().Contain(r => r.Status == "Approaching Deadline");
    }
}
