using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AppealsService.Controllers;
using AppealsService.Models;

namespace AppealsService.Tests.Integration;

/// <summary>
/// <c>POST /api/appeals/{id}/notices</c>: recording when the
/// acknowledgment and resolution letters went out.
/// </summary>
public class AppealNoticeTests : IClassFixture<AppealsWebApplicationFactory>
{
    private readonly AppealsWebApplicationFactory _factory;

    public AppealNoticeTests(AppealsWebApplicationFactory factory) => _factory = factory;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private HttpClient NewClient()
    {
        var client = _factory.CreateDefaultClient(new CloudHealthOffice.Infrastructure.Security.ChoDevelopmentTokenHandler());
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-notice");
        return client;
    }

    private static async Task<Appeal> ReadAppealAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Request failed: {(int)response.StatusCode}. Body: {body}");
        return JsonSerializer.Deserialize<Appeal>(body, JsonOptions)!;
    }

    private static async Task<Appeal> CreateAsync(HttpClient client) =>
        await ReadAppealAsync(await client.PostAsJsonAsync("/api/appeals", new CreateAppealRequest
        {
            ClaimId = "CLM-N-1",
            ClaimNumber = "CLM-N-1",
            MemberId = "M-N-1",
            PatientName = "Jane Doe",
            ProviderNPI = "1234567890",
            AppealReason = "Wait time.",
            LineOfBusiness = LineOfBusiness.Medicaid,
            AppealType = AppealType.Reconsideration,
            AppealLevel = AppealLevel.FirstLevel
        }, JsonOptions));

    private static Task<HttpResponseMessage> Post(HttpClient client, string id, AppealNoticeKind notice, DateTime sentAt, string? eventId = null) =>
        client.PostAsJsonAsync($"/api/appeals/{id}/notices",
            new RecordNoticeSentRequest { Notice = notice, SentAt = sentAt, EventId = eventId }, JsonOptions);

    private List<AppealEvent> NoticeEvents(string id) =>
        _factory.Repo.SnapshotEvents().Where(e => e.AppealId == id && e.EventType == AppealEventType.AppealNoticeSent).ToList();

    [Fact]
    public async Task Records_both_letters_once_and_audits_each()
    {
        _factory.Reset();
        var client = NewClient();
        var draft = await CreateAsync(client);

        (await Post(client, draft.Id, AppealNoticeKind.Acknowledgment, DateTime.UtcNow)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "a draft has not been received");

        var submitted = await ReadAppealAsync(await client.PostAsJsonAsync($"/api/appeals/{draft.Id}/submit", new IdempotencyEnvelope(), JsonOptions));
        var ackAt = submitted.SubmittedDate.ToUniversalTime().AddSeconds(1);
        var acked = await ReadAppealAsync(await Post(client, draft.Id, AppealNoticeKind.Acknowledgment, ackAt, "ack-1"));
        acked.AcknowledgmentSentAt!.Value.ToUniversalTime().Should().BeCloseTo(ackAt, TimeSpan.FromMilliseconds(1));
        acked.Status.Should().Be(AppealStatus.Submitted, "recording a letter is not a status transition");

        // Same date again, and a same-EventId retry: no second event.
        (await Post(client, draft.Id, AppealNoticeKind.Acknowledgment, ackAt)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post(client, draft.Id, AppealNoticeKind.Acknowledgment, ackAt.AddSeconds(1), "ack-1")).StatusCode.Should().Be(HttpStatusCode.OK);
        NoticeEvents(draft.Id).Should().ContainSingle();

        (await Post(client, draft.Id, AppealNoticeKind.Resolution, DateTime.UtcNow)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "the case is not closed yet");

        await ReadAppealAsync(await client.PostAsJsonAsync($"/api/appeals/{draft.Id}/begin-review", new IdempotencyEnvelope(), JsonOptions));
        await ReadAppealAsync(await client.PostAsJsonAsync($"/api/appeals/{draft.Id}/close", new CloseAppealRequest
        {
            ClosureReasonCode = AppealClosureReasonCode.Approved,
            Decision = new AppealDecisionInput { DecisionType = AppealDecisionType.Approved, DecisionReason = "Booked." }
        }, JsonOptions));

        var letterAt = DateTime.UtcNow.AddSeconds(1);
        var resolved = await ReadAppealAsync(await Post(client, draft.Id, AppealNoticeKind.Resolution, letterAt));
        resolved.ResolutionNoticeSentAt!.Value.ToUniversalTime().Should().BeCloseTo(letterAt, TimeSpan.FromMilliseconds(1));
        resolved.AcknowledgmentSentAt.Should().NotBeNull("recording one letter keeps the other");

        var events = NoticeEvents(draft.Id);
        events.Should().HaveCount(2);
        events[1].Payload!["notice"]!.GetValue<string>().Should().Be("Resolution");
        _factory.Publisher.NoticesSent.Select(p => p.Notice).Should().Equal("Acknowledgment", "Resolution");
    }

    [Fact]
    public async Task A_corrected_date_replaces_the_old_one_and_keeps_it_in_the_audit_row()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);
        var submitted = await ReadAppealAsync(await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions));
        var first = submitted.SubmittedDate.ToUniversalTime().AddSeconds(1);
        var second = first.AddSeconds(30);

        await ReadAppealAsync(await Post(client, appeal.Id, AppealNoticeKind.Acknowledgment, first));
        var corrected = await ReadAppealAsync(await Post(client, appeal.Id, AppealNoticeKind.Acknowledgment, second));

        corrected.AcknowledgmentSentAt!.Value.ToUniversalTime().Should().BeCloseTo(second, TimeSpan.FromMilliseconds(1));
        var last = NoticeEvents(appeal.Id).Last();
        DateTime.Parse(last.Payload!["previousSentAt"]!.GetValue<string>()).ToUniversalTime()
            .Should().BeCloseTo(first, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Refuses_a_future_date_or_one_before_submission()
    {
        _factory.Reset();
        var client = NewClient();
        var appeal = await CreateAsync(client);
        var submitted = await ReadAppealAsync(await client.PostAsJsonAsync($"/api/appeals/{appeal.Id}/submit", new IdempotencyEnvelope(), JsonOptions));

        (await Post(client, appeal.Id, AppealNoticeKind.Acknowledgment, DateTime.UtcNow.AddDays(1))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await Post(client, appeal.Id, AppealNoticeKind.Acknowledgment, submitted.SubmittedDate.ToUniversalTime().AddDays(-1))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        NoticeEvents(appeal.Id).Should().BeEmpty();
    }
}
