using System.Net;
using System.Text;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The portal's client for the held-275 queue. The distinction it exists to
/// preserve is null (we could not ask) versus empty (we asked and nothing is
/// held) — collapsing those would let an outage read as a clean queue. It
/// also carries attachment-service's own refusal text through, because the
/// billing-NPI refusal explains itself and a status code does not.
/// </summary>
public class UnmatchedAttachmentServiceTests
{
    private const string BaseUrl = "http://attachment-service";

    private static UnmatchedAttachmentService Service(StubHandler handler) =>
        new(new HttpClient(handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [UnmatchedAttachmentService.BaseUrlSetting] = BaseUrl,
                })
                .Build(),
            NullLogger<UnmatchedAttachmentService>.Instance);

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = "[]";
        public string? ContentType { get; set; } = "application/json";
        public Exception? Throw { get; set; }
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Throw is not null) throw Throw;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, ContentType ?? "text/plain"),
            });
        }
    }

    [Fact]
    public async Task Nothing_held_is_an_empty_list_not_null()
    {
        var held = await Service(new StubHandler { Body = "[]" }).GetHeldAsync();

        Assert.NotNull(held);
        Assert.Empty(held);
    }

    [Fact]
    public async Task An_unreachable_service_is_null_not_an_empty_list()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("down") };

        Assert.Null(await Service(handler).GetHeldAsync());
    }

    [Fact]
    public async Task A_refused_listing_is_null_not_an_empty_list()
    {
        var handler = new StubHandler { Status = HttpStatusCode.Forbidden, Body = "" };

        Assert.Null(await Service(handler).GetHeldAsync());
    }

    [Fact]
    public async Task Held_attachments_are_mapped_and_ordered_oldest_first()
    {
        var handler = new StubHandler
        {
            Body = """
                   [
                     {"id":"att-new","attachmentControlNumber":"ACN-2","providerId":"1234567893",
                      "providerName":"Synthetic Clinic","patientFirstName":"Pat","patientLastName":"Example",
                      "documentType":"Medical Records","createdDate":"2026-10-20T00:00:00Z"},
                     {"id":"att-old","attachmentControlNumber":"ACN-1","providerId":"1234567893",
                      "documentType":"Itemized Bill","createdDate":"2026-10-01T00:00:00Z"}
                   ]
                   """,
        };

        var held = await Service(handler).GetHeldAsync();

        Assert.Collection(held!,
            first =>
            {
                Assert.Equal("att-old", first.Id);
                Assert.Equal("ACN-1", first.AttachmentControlNumber);
            },
            second =>
            {
                Assert.Equal("att-new", second.Id);
                Assert.Equal("Pat Example", second.PatientName);
            });
    }

    [Fact]
    public async Task An_attachment_with_no_patient_name_shows_a_dash_rather_than_a_blank()
    {
        var handler = new StubHandler
        {
            Body = """
                   [{"id":"att-1","providerId":"1234567893","documentType":"Records",
                     "createdDate":"2026-10-01T00:00:00Z"}]
                   """,
        };

        var held = await Service(handler).GetHeldAsync();

        Assert.Equal("—", held!.Single().PatientName);
    }

    [Fact]
    public async Task Linking_posts_the_claim_and_the_reason()
    {
        var handler = new StubHandler { Body = "{}" };

        var outcome = await Service(handler).LinkToClaimAsync("att-1", "CLM-42", "Same patient.");

        Assert.True(outcome.Linked);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{BaseUrl}/attachments/att-1/link", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_refusal_keeps_the_services_own_wording()
    {
        var handler = new StubHandler
        {
            Status = HttpStatusCode.Conflict,
            ContentType = "text/plain",
            Body = "That claim's billing provider is not the provider who sent this attachment.",
        };

        var outcome = await Service(handler).LinkToClaimAsync("att-1", "CLM-OTHER", null);

        Assert.False(outcome.Linked);
        Assert.Contains("billing provider", outcome.Problem);
    }

    [Fact]
    public async Task A_problem_details_refusal_is_unwrapped()
    {
        var handler = new StubHandler
        {
            Status = HttpStatusCode.BadGateway,
            Body = """{"title":"Bad gateway","detail":"Could not reach claims-service; nothing was linked."}""",
        };

        var outcome = await Service(handler).LinkToClaimAsync("att-1", "CLM-42", null);

        Assert.Equal("Could not reach claims-service; nothing was linked.", outcome.Problem);
    }

    [Fact]
    public async Task An_unreachable_service_says_nothing_was_linked()
    {
        var handler = new StubHandler { Throw = new HttpRequestException("down") };

        var outcome = await Service(handler).LinkToClaimAsync("att-1", "CLM-42", null);

        Assert.False(outcome.Linked);
        Assert.Contains("Nothing was linked", outcome.Problem);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 20)]
    [InlineData(31, -1)]
    public void Days_left_counts_down_from_the_hold_period(int daysHeld, int expected)
    {
        var now = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        var attachment = new HeldAttachment(
            "att-1", "ACN-1", "1234567893", null, null, null, "Records", now.AddDays(-daysHeld), null);

        Assert.Equal(expected, attachment.DaysLeft(now, holdDays: 30));
    }
}
