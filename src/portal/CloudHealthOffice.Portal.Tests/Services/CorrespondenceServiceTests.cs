using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class CorrespondenceServiceTests
{
    private readonly Mock<ILogger<CorrespondenceService>> _logger = new();
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:CorrespondenceService"] = "http://correspondence:8080/"
        })
        .Build();

    private CorrespondenceService CreateService(RecordingHandler handler) =>
        new(new HttpClient(handler), _configuration, _logger.Object);

    [Fact]
    public async Task Calls_correspondence_service_routes()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(handler);

        await sut.GetRecentLettersAsync("APPEAL_ACK", "CheckFailed", 25);
        await sut.GetLetterTypesAsync();
        await sut.GetTemplatesAsync();

        handler.Requests.Should().Equal(
            "http://correspondence:8080/api/v1/correspondence/letters/recent?limit=25&letterTypeCode=APPEAL_ACK&status=CheckFailed",
            "http://correspondence:8080/api/v1/correspondence/letter-types",
            "http://correspondence:8080/api/v1/correspondence/templates");
    }

    [Fact]
    public async Task Reads_the_summary_and_letters()
    {
        var summary = await CreateService(new RecordingHandler(HttpStatusCode.OK,
            """{"renderedToday":2,"renderedLast7Days":9,"checkFailedLast7Days":1,"asOf":"2026-10-21T00:00:00Z"}""")).GetSummaryAsync();
        summary.RenderedLast7Days.Should().Be(9);
        summary.CheckFailedLast7Days.Should().Be(1);

        var letters = await CreateService(new RecordingHandler(HttpStatusCode.OK,
            """[{"id":"LTR-1","letterTypeCode":"APPEAL_ACK","status":"CheckFailed","letterDate":"2026-10-21","subjects":[{"kind":"Appeal","id":"APL-1"}],"checks":[{"name":"RecipientAddressComplete","passed":false,"detail":"ZIP code"}]}]""")).GetRecentLettersAsync();
        letters.Should().ContainSingle().Which.Status.Should().Be("CheckFailed");
        letters[0].LetterDate.Should().Be(new DateOnly(2026, 10, 21));
        letters[0].Checks.Single().Detail.Should().Be("ZIP code");
    }

    [Fact]
    public async Task Unreachable_service_is_reported_by_name()
    {
        var sut = CreateService(new RecordingHandler(HttpStatusCode.InternalServerError, ""));
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetSummaryAsync());
        ex.ServiceName.Should().Be(CorrespondenceService.ServiceName);
    }

    public sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
