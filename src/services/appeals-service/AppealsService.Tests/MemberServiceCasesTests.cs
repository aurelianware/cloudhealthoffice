using AppealsService.Controllers;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using AppealsService.Services.RulePacks;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace AppealsService.Tests;

public class MemberServiceCasesTests
{
    private readonly Mock<IMemberServiceCaseStore> _store = new();
    private readonly Mock<IContactIntakeReader> _contacts = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly Mock<IAppealHolidayCalendarProvider> _holidays = new();
    private readonly Mock<IAppealFieldEncryptor> _crypto = new();
    private readonly Mock<IMemberCaseSubjectValidator> _subjects = new();
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
        RulePackCatalog.FromEmbeddedResources(), _holidays.Object, _crypto.Object, _subjects.Object);

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
        _store.Setup(s => s.RecordDecisionAsync("t1", "case-1", 4, "encrypted-synthetic", "rep-1",
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 4, Resolution = "Synthetic review outcome" }, default);
        result.Should().BeOfType<NoContentResult>();
        var stale = await Controller().RecordDecision("case-1", new MemberCaseDecisionRequest {
            Revision = 3, Resolution = "Stale outcome" }, default);
        stale.Should().BeOfType<ConflictObjectResult>();
    }

}
