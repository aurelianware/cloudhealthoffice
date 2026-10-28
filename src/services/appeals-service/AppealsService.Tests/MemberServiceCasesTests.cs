using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Services.RulePacks;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppealsService.Tests;

public class MemberServiceCasesTests
{
    private readonly Mock<IMemberServiceCaseStore> _store = new();
    private readonly Mock<IContactIntakeReader> _contacts = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly Mock<IAppealHolidayCalendarProvider> _holidays = new();
    private readonly Mock<IAppealFieldEncryptor> _crypto = new();
    private readonly Mock<IMemberCaseSubjectValidator> _subjects = new();
    private readonly Mock<IAppealDecisionReporter> _reporter = new();
    private readonly ContactIntakeReceipt _contact = new() {
        Id = "contact-1", TenantId = "t1", MemberId = "synthetic-member", Classification = "Grievance",
        Caller = new() { Type = "Member" }, Summary = "Synthetic access concern",
        StartedAt = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc),
        CaseIntent = new() { State = "CA", LineOfBusiness = "Medicaid" }
    };
    public MemberServiceCasesTests()
    {
        _actor.SetupGet(a => a.TenantId).Returns("t1");
        _actor.SetupGet(a => a.UserId).Returns("rep-1");
        _contacts.Setup(c => c.GetAsync("contact-1", It.IsAny<CancellationToken>())).ReturnsAsync(_contact);
        _crypto.Setup(c => c.EncryptAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("encrypted-synthetic");
        _store.Setup(s => s.CreateOrReadAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemberServiceCase c, CancellationToken _) => c);
    }
    private MemberServiceCasesController Controller() => new(_store.Object, _contacts.Object, _actor.Object,
        RulePackCatalog.FromEmbeddedResources(), _holidays.Object, _crypto.Object, _subjects.Object,
        new AppealDecisionReporting(_reporter.Object, _store.Object, NullLogger<AppealDecisionReporting>.Instance));

    private void StoredCase(string classification, string? authorizationId = null) =>
        _store.Setup(s => s.GetAsync("t1", "case-1", It.IsAny<CancellationToken>())).ReturnsAsync(new MemberServiceCase {
            Id = "case-1", TenantId = "t1", Classification = classification, AuthorizationId = authorizationId,
            Status = "Received", Revision = 4 });

    [Fact]
    public async Task Grievance_has_no_fabricated_claim_and_uses_original_receipt()
    {
        MemberServiceCase? saved = null;
        _store.Setup(s => s.CreateOrReadAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .Callback((MemberServiceCase c, CancellationToken _) => saved = c)
            .ReturnsAsync((MemberServiceCase c, CancellationToken _) => c);
        var result = await Controller().Create("contact-1", default);
        result.Result.Should().BeOfType<OkObjectResult>();
        saved!.ReceivedAt.Should().Be(_contact.StartedAt);
        saved.MemberId.Should().Be(_contact.MemberId);
        saved.EncryptedSummary.Should().Be("encrypted-synthetic");
        saved.Receipt.RulePackVersion.Should().NotBeNullOrEmpty();
        saved.Receipt.IntakeHash.Should().Be(ContactIntakeIdentity.Hash(_contact.CaseIntent!));
        saved.Receipt.DueAt.Should().BeAfter(_contact.StartedAt);
    }

    [Fact]
    public async Task Wrong_tenant_receipt_is_not_used()
    {
        _contact.TenantId = "other";
        (await Controller().Create("contact-1", default)).Result.Should().BeOfType<NotFoundResult>();
        _store.Verify(s => s.CreateOrReadAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Existing_case_returns_its_pinned_receipt()
    {
        var receipt = new ContactCaseResult { Id = "saved-id", RulePackVersion = "pinned" };
        _store.Setup(s => s.GetAsync("t1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemberServiceCase { Receipt = receipt });
        var result = (await Controller().Create("contact-1", default)).Result.Should().BeOfType<OkObjectResult>().Subject;
        result.Value.Should().BeSameAs(receipt);
        _store.Verify(s => s.CreateOrReadAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unsupported_jurisdiction_does_not_get_guessed_deadlines()
    {
        _contact.CaseIntent!.State = "ZZ";
        (await Controller().Create("contact-1", default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        _store.Verify(s => s.CreateOrReadAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Appeal_requires_a_verified_pre_service_medical_subject()
    {
        _contact.Classification = "Appeal";
        (await Controller().Create("contact-1", default)).Result.Should().BeOfType<UnprocessableEntityObjectResult>();
        _store.Verify(s => s.CreateOrReadAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task Recording_a_review_outcome_requires_a_matching_revision_and_does_not_close_case()
    {
        StoredCase("Grievance");
        _store.Setup(s => s.RecordDecisionAsync("t1", "case-1", 4, "encrypted-synthetic", "rep-1",
            It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 4, Resolution = "Synthetic review outcome" }, default);
        result.Should().BeOfType<NoContentResult>();
        var stale = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 3, Resolution = "Stale outcome" }, default);
        stale.Should().BeOfType<ConflictObjectResult>();
    }

    [Theory]
    [InlineData("Appeal", null)]
    [InlineData("ExpeditedAppeal", (MemberAppealOutcome)99)]
    [InlineData("Grievance", MemberAppealOutcome.Upheld)]
    public async Task Appeal_needs_a_structured_outcome_and_a_grievance_has_none(string classification, MemberAppealOutcome? outcome)
    {
        StoredCase(classification, "auth-1");
        var result = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 4, Resolution = "Synthetic review outcome", Outcome = outcome }, default);
        result.Should().BeOfType<BadRequestObjectResult>();
        _store.Verify(s => s.RecordDecisionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<MemberAppealOutcome?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unknown_case_is_not_found()
    {
        (await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 4, Resolution = "Synthetic", Outcome = MemberAppealOutcome.Upheld }, default))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Appeal_outcome_is_stored_and_reported_to_the_authorization_ledger()
    {
        StoredCase("Appeal", "auth-1");
        _store.Setup(s => s.RecordDecisionAsync("t1", "case-1", 4, "encrypted-synthetic", "rep-1",
            It.IsAny<DateTime>(), MemberAppealOutcome.Overturned, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        MemberServiceCase? sent = null;
        _reporter.Setup(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .Callback((MemberServiceCase c, CancellationToken _) => sent = c)
            .ReturnsAsync((AppealDecisionDelivery.Accepted, (string?)null));

        var result = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 4, Resolution = "Synthetic overturn", Outcome = MemberAppealOutcome.Overturned }, default);

        result.Should().BeOfType<NoContentResult>();
        sent!.DecisionOutcome.Should().Be(MemberAppealOutcome.Overturned);
        sent.DecisionRecordedAt.Should().NotBeNull();
        _store.Verify(s => s.RecordReportingAttemptAsync("t1", "case-1", It.IsNotNull<DateTime?>(), null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Decision_stands_when_the_ledger_is_unreachable_and_is_left_for_the_sweep()
    {
        StoredCase("Appeal", "auth-1");
        _store.Setup(s => s.RecordDecisionAsync("t1", "case-1", 4, "encrypted-synthetic", "rep-1",
            It.IsAny<DateTime>(), MemberAppealOutcome.Upheld, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _reporter.Setup(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("synthetic"));

        var result = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 4, Resolution = "Synthetic uphold", Outcome = MemberAppealOutcome.Upheld }, default);

        result.Should().BeOfType<NoContentResult>();
        _store.Verify(s => s.RecordReportingAttemptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>(),
            It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Reporting_state_follows_the_ledger_delivery()
    {
        var row = new MemberServiceCase { AuthorizationId = "auth-1", DecisionOutcome = MemberAppealOutcome.Upheld };
        MemberServiceCasesController.ReportingState(row).Should().Be("Pending");
        row.ReportingRejectedReason = "refused";
        MemberServiceCasesController.ReportingState(row).Should().Be("NeedsAttention");
        row.ReportedToAuthorizationAt = DateTime.UtcNow;
        MemberServiceCasesController.ReportingState(row).Should().Be("Reported");
        MemberServiceCasesController.ReportingState(new MemberServiceCase()).Should().BeNull();
    }

    private MemberServiceCase Refused(string reason = "authorization-service refused the fact (400).") => new() {
        Id = "case-1", TenantId = "t1", Classification = "Appeal", AuthorizationId = "auth-1",
        Status = "DecisionRecorded", DecisionOutcome = MemberAppealOutcome.Overturned,
        DecisionRecordedAt = new DateTime(2026, 10, 27, 15, 30, 0, DateTimeKind.Utc),
        ReportingAttempts = 1, ReportingRejectedReason = reason };

    [Fact]
    public async Task Requeue_clears_the_refusal_and_reports_the_outcome_now()
    {
        var refused = Refused();
        _store.SetupSequence(s => s.GetAsync("t1", "case-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(refused)
            .ReturnsAsync(new MemberServiceCase { AuthorizationId = "auth-1", DecisionOutcome = MemberAppealOutcome.Overturned,
                ReportedToAuthorizationAt = DateTime.UtcNow });
        _store.Setup(s => s.RequeueRefusedReportingAsync(refused, "rep-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        MemberServiceCase? sent = null;
        _reporter.Setup(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()))
            .Callback((MemberServiceCase c, CancellationToken _) => sent = c)
            .ReturnsAsync((AppealDecisionDelivery.Accepted, (string?)null));

        var result = (await Controller().RequeueReporting("case-1", default)).Should().BeOfType<OkObjectResult>().Subject;

        System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Contain("\"Reporting\":\"Reported\"");
        sent!.ReportingRejectedReason.Should().BeNull();
        sent.ReportingAttempts.Should().Be(0);
        _store.Verify(s => s.RecordReportingAttemptAsync("t1", "case-1", It.IsNotNull<DateTime?>(), null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Only_a_refused_outcome_can_be_requeued()
    {
        var pending = Refused(); pending.ReportingRejectedReason = null;
        _store.Setup(s => s.GetAsync("t1", "case-1", It.IsAny<CancellationToken>())).ReturnsAsync(pending);
        (await Controller().RequeueReporting("case-1", default)).Should().BeOfType<ConflictObjectResult>();

        // Someone else re-queued it between the read and the write.
        _store.Setup(s => s.GetAsync("t1", "case-1", It.IsAny<CancellationToken>())).ReturnsAsync(Refused());
        _store.Setup(s => s.RequeueRefusedReportingAsync(It.IsAny<MemberServiceCase>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        (await Controller().RequeueReporting("case-1", default)).Should().BeOfType<ConflictObjectResult>();

        _reporter.Verify(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()), Times.Never);
        (await Controller().RequeueReporting("other", default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Tenant_requeue_leaves_the_refused_outcomes_for_the_sweep()
    {
        var a = Refused(); var b = Refused("Gave up after 24 attempts; last: authorization-service answered 503."); b.Id = "case-2";
        _store.Setup(s => s.ListReportingNeedsAttentionAsync("t1", 500, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { a, b });
        _store.Setup(s => s.RequeueRefusedReportingAsync(a, "rep-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _store.Setup(s => s.RequeueRefusedReportingAsync(b, "rep-1", It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var result = (await Controller().RequeueAllReporting(default)).Should().BeOfType<OkObjectResult>().Subject;

        System.Text.Json.JsonSerializer.Serialize(result.Value).Should().Be("{\"Requeued\":1}");
        _reporter.Verify(r => r.SendAsync(It.IsAny<MemberServiceCase>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
