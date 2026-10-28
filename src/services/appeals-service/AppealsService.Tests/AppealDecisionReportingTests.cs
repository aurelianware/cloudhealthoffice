using System.Net;
using System.Text.Json;
using AppealsService.HostedServices;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests;

public class AppealDecisionReportingTests
{
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
            { BaseAddress = new Uri("http://authorization-service/") };
    }

    private static MemberServiceCase Decided(MemberAppealOutcome outcome = MemberAppealOutcome.Overturned) => new()
    {
        Id = "contact-0123456789abcdef0123456789abcdef", TenantId = "t1", Classification = "Appeal",
        AuthorizationId = "auth/1", Status = "DecisionRecorded", DecisionOutcome = outcome,
        DecisionRecordedAt = new DateTime(2026, 10, 27, 15, 30, 0, DateTimeKind.Utc),
    };

    [Theory]
    [InlineData(MemberAppealOutcome.Upheld, "Denied")]
    [InlineData(MemberAppealOutcome.Overturned, "Approved")]
    [InlineData(MemberAppealOutcome.PartiallyOverturned, "Modified")]
    public async Task Sends_an_appeal_decision_fact_as_the_service_for_the_case_tenant(MemberAppealOutcome outcome, string ledger)
    {
        var handler = new Handler(HttpStatusCode.OK);
        var (delivery, _) = await new HttpAppealDecisionReporter(new Clients(handler)).SendAsync(Decided(outcome), default);

        delivery.Should().Be(AppealDecisionDelivery.Accepted);
        handler.Request!.Method.Should().Be(HttpMethod.Post);
        handler.Request.RequestUri!.AbsolutePath.Should().Be("/api/authorizations/cms-0057/auth%2F1/facts");
        handler.Request.Headers.GetValues("X-Tenant-ID").Should().Equal("t1");
        handler.Request.Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var service).Should().BeTrue();
        service.Should().BeTrue();
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        body.GetProperty("kind").GetString().Should().Be("AppealDecision");
        body.GetProperty("outcome").GetString().Should().Be(ledger);
        body.GetProperty("sourceSystem").GetString().Should().Be("appeals-service");
        body.GetProperty("eventId").GetString().Should().Be("service-case:contact-0123456789abcdef0123456789abcdef");
        body.GetProperty("evidenceReference").GetString().Should().Be("service-case:contact-0123456789abcdef0123456789abcdef");
        body.GetProperty("effectiveAt").GetDateTimeOffset().Should().Be(new DateTimeOffset(2026, 10, 27, 15, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, AppealDecisionDelivery.Rejected)]
    [InlineData(HttpStatusCode.NotFound, AppealDecisionDelivery.Rejected)]
    [InlineData(HttpStatusCode.Conflict, AppealDecisionDelivery.RetryLater)]
    [InlineData(HttpStatusCode.Forbidden, AppealDecisionDelivery.RetryLater)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AppealDecisionDelivery.RetryLater)]
    public async Task Classifies_refusals_and_transient_failures(HttpStatusCode status, AppealDecisionDelivery expected)
    {
        var (delivery, reason) = await new HttpAppealDecisionReporter(new Clients(new Handler(status))).SendAsync(Decided(), default);
        delivery.Should().Be(expected);
        reason.Should().Contain(((int)status).ToString());
    }

    [Fact]
    public async Task A_case_without_an_appeal_outcome_is_never_sent()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var c = Decided(); c.DecisionOutcome = null;
        (await new HttpAppealDecisionReporter(new Clients(handler)).SendAsync(c, default)).Delivery
            .Should().Be(AppealDecisionDelivery.Rejected);
        handler.Request.Should().BeNull();
    }

    [Fact]
    public async Task Retries_back_off_and_give_up_after_the_attempt_limit()
    {
        var store = new Mock<IMemberServiceCaseStore>();
        var reporter = new Mock<IAppealDecisionReporter>();
        reporter.Setup(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AppealDecisionDelivery.RetryLater, "authorization-service answered 503."));
        var reporting = new AppealDecisionReporting(reporter.Object, store.Object, NullLogger<AppealDecisionReporting>.Instance);

        var first = Decided();
        await reporting.DeliverAsync(first, default);
        store.Verify(s => s.RecordReportingAttemptAsync("t1", first.Id, null, null,
            It.Is<DateTime?>(d => d > DateTime.UtcNow), It.IsAny<CancellationToken>()), Times.Once);

        var last = Decided(); last.ReportingAttempts = AppealDecisionReporting.MaxAttempts - 1;
        await reporting.DeliverAsync(last, default);
        store.Verify(s => s.RecordReportingAttemptAsync("t1", last.Id, null,
            It.Is<string?>(r => r != null && r.StartsWith("Gave up")), null, It.IsAny<CancellationToken>()), Times.Once);

        AppealDecisionReporting.Backoff(1).Should().Be(TimeSpan.FromMinutes(1));
        AppealDecisionReporting.Backoff(4).Should().Be(TimeSpan.FromMinutes(8));
        AppealDecisionReporting.Backoff(30).Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Sweep_delivers_each_owed_case()
    {
        var store = new Mock<IMemberServiceCaseStore>();
        store.Setup(s => s.ListUnreportedAppealDecisionsAsync(It.IsAny<DateTime>(), 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Decided(), Decided(MemberAppealOutcome.Upheld) });
        var reporter = new Mock<IAppealDecisionReporter>();
        reporter.SetupSequence(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AppealDecisionDelivery.Accepted, (string?)null))
            .ReturnsAsync((AppealDecisionDelivery.Rejected, "authorization-service refused the fact (400)."));
        var sweep = new AppealDecisionReportingSweep(store.Object,
            new AppealDecisionReporting(reporter.Object, store.Object, NullLogger<AppealDecisionReporting>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<AppealDecisionReportingSweep>.Instance);

        (await sweep.SweepAsync(default)).Should().Be(1);
        store.Verify(s => s.RecordReportingAttemptAsync("t1", It.IsAny<string>(), null,
            "authorization-service refused the fact (400).", null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
