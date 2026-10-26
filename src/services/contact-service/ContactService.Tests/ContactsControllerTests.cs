using CloudHealthOffice.Infrastructure.Security;
using ContactService.Controllers;
using ContactService.Models;
using ContactService.Repositories;
using ContactService.Services.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ContactService.Tests;

/// <summary>
/// The controller over a substituted repository: what it stamps onto a
/// contact, what it refuses, and what it does when the claim note or a
/// competing write gets in the way.
/// </summary>
public class ContactsControllerTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
    private const string ActorId = "rep-1";
    private const string TenantId = "t1";

    private readonly Mock<IContactRepository> _contacts = new(MockBehavior.Strict);
    private readonly Mock<IContactReasonRepository> _reasons = new();
    private readonly Mock<IContactNoteGateway> _notes = new();

    public ContactsControllerTests()
    {
        _reasons.Setup(r => r.ListAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(StarterContactReasons.All.Select(s => new ContactReason
                {
                    Code = s.Code, Label = s.Label, Category = s.Category, IsActive = true,
                }).ToList());

        _notes.Setup(n => n.WriteContactNoteAsync(It.IsAny<ContactNoteCommand>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string?)null);
    }

    private ContactsController Controller()
    {
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.UserId).Returns(ActorId);
        actor.SetupGet(a => a.DisplayName).Returns("A Rep");
        actor.SetupGet(a => a.TenantId).Returns(TenantId);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = TenantId;

        return new ContactsController(
            _contacts.Object, _reasons.Object, _notes.Object, actor.Object,
            NullLogger<ContactsController>.Instance, new FixedClock(Now))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static Contact Stored(
        ContactStatus status = ContactStatus.Open,
        long revision = 1,
        CallerType callerType = CallerType.Member) => new()
    {
        Id = "contact-1",
        TenantId = TenantId,
        ContactNumber = "C-20261022-AAAA1111",
        Revision = revision,
        Status = status,
        Channel = ContactChannel.Phone,
        Direction = ContactDirection.Inbound,
        Caller = new ContactCaller { Type = callerType, Name = "Ada Lovelace" },
        MemberId = "M1",
        StartedAt = Now.AddMinutes(-6),
        AgentId = ActorId,
    };

    private void Exists(Contact contact) =>
        _contacts.Setup(c => c.GetByIdAsync(contact.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(contact);

    private void SavesSuccessfully() =>
        _contacts.Setup(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Contact c, CancellationToken _) => c);

    // ── Starting ──────────────────────────────────────────────────

    [Fact]
    public async Task Start_takes_the_agent_and_the_clock_from_the_server()
    {
        Contact? created = null;
        _contacts.Setup(c => c.CreateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
                 .Callback((Contact c, CancellationToken _) => created = c)
                 .ReturnsAsync((Contact c, CancellationToken _) => c);

        var result = await Controller().Start(new StartContactRequest
        {
            Channel = ContactChannel.Phone,
            Caller = new ContactCaller { Type = CallerType.Member, Name = "Ada" },
        }, default);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        created.Should().NotBeNull();
        created!.AgentId.Should().Be(ActorId);
        created.AgentName.Should().Be("A Rep");
        created.StartedAt.Should().Be(Now);
        created.Status.Should().Be(ContactStatus.Open);
        created.ContactNumber.Should().StartWith("C-20261022-");
        created.History.Should().ContainSingle()
               .Which.Action.Should().Be("Started");
    }

    [Fact]
    public async Task Start_refuses_a_contact_that_begins_in_the_future()
    {
        var result = await Controller().Start(new StartContactRequest
        {
            StartedAt = Now.AddHours(1),
        }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _contacts.Verify(c => c.CreateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Start_keeps_only_the_reason_codes_the_tenant_has()
    {
        Contact? created = null;
        _contacts.Setup(c => c.CreateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
                 .Callback((Contact c, CancellationToken _) => created = c)
                 .ReturnsAsync((Contact c, CancellationToken _) => c);

        await Controller().Start(new StartContactRequest
        {
            ReasonCodes = ["claim_status", "NOT_A_REASON", "CLAIM_STATUS"],
        }, default);

        // Upper-cased, de-duplicated, and the unknown one dropped rather than
        // rejected: a rep mid-call is not stopped by a stale taxonomy.
        created!.ReasonCodes.Should().Equal("CLAIM_STATUS");
    }

    // ── Reading ───────────────────────────────────────────────────

    [Fact]
    public async Task A_contact_that_does_not_exist_is_a_404_on_every_route()
    {
        _contacts.Setup(c => c.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Contact?)null);

        var controller = Controller();

        (await controller.GetById("nope", default)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.Update("nope", new UpdateContactRequest { Revision = 1 }, default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.LinkSubject("nope",
            new LinkSubjectRequest { Revision = 1, SubjectId = "claim-7" }, default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.Close("nope",
            new CloseContactRequest { Revision = 1, Resolution = ContactResolution.ResolvedOnCall }, default))
            .Result.Should().BeOfType<NotFoundResult>();
        (await controller.Reopen("nope", new ReopenContactRequest { Revision = 1 }, default))
            .Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_contact_is_summarised_for_a_list_without_its_conversation()
    {
        var contact = Stored();
        contact.Summary = "the whole conversation";
        contact.DurationSeconds = 272;
        contact.Subjects.Add(new ContactSubject
        {
            Type = ContactSubjectType.Claim, SubjectId = "claim-7", LinkedAt = Now,
        });
        _contacts.Setup(c => c.ListByMemberAsync("M1", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync([contact]);

        var result = await Controller().ListByMember("M1", 50, default);

        var summary = result.Result.Should().BeOfType<OkObjectResult>()
              .Which.Value.Should().BeAssignableTo<IReadOnlyList<ContactSummary>>()
              .Which.Should().ContainSingle().Subject;

        summary.ContactNumber.Should().Be("C-20261022-AAAA1111");
        summary.CallerName.Should().Be("Ada Lovelace");
        summary.SubjectCount.Should().Be(1);
        summary.DurationSeconds.Should().Be(272);
        summary.AgentId.Should().Be(ActorId);
    }

    [Fact]
    public async Task A_provider_history_and_a_subject_search_each_summarise_too()
    {
        _contacts.Setup(c => c.ListByProviderAsync("1234567893", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync([Stored(callerType: CallerType.Provider)]);
        _contacts.Setup(c => c.ListBySubjectAsync(
                     ContactSubjectType.Claim, "claim-7", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync([Stored()]);

        var controller = Controller();

        (await controller.ListByProvider("1234567893", 50, default)).Result
            .Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IReadOnlyList<ContactSummary>>()
            .Which.Should().ContainSingle();

        (await controller.ListBySubject(ContactSubjectType.Claim, "claim-7", 50, default)).Result
            .Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeAssignableTo<IReadOnlyList<ContactSummary>>()
            .Which.Should().ContainSingle();
    }

    [Fact]
    public async Task A_rep_asks_for_their_own_contacts_not_someone_elses()
    {
        _contacts.Setup(c => c.ListByAgentAsync(
                     It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync([]);

        await Controller().ListMine(openOnly: true, limit: 25, default);

        // The agent comes from the token, never from the query string.
        _contacts.Verify(c => c.ListByAgentAsync(ActorId, true, 25, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_contact_is_fetched_by_its_id()
    {
        Exists(Stored());

        (await Controller().GetById("contact-1", default)).Result
            .Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<Contact>()
            .Which.ContactNumber.Should().Be("C-20261022-AAAA1111");
    }

    // ── Working it ────────────────────────────────────────────────

    [Fact]
    public async Task Update_rejects_a_stale_revision_without_writing()
    {
        Exists(Stored(revision: 4));

        var result = await Controller().Update("contact-1",
            new UpdateContactRequest { Revision = 3, Summary = "late" }, default);

        result.Result.Should().BeOfType<ConflictObjectResult>();
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_refuses_to_change_a_closed_contact()
    {
        Exists(Stored(ContactStatus.Closed));

        var result = await Controller().Update("contact-1",
            new UpdateContactRequest { Revision = 1, Summary = "after the fact" }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_records_who_classified_the_contact_and_when()
    {
        var contact = Stored();
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1,
            Classification = ContactClassification.Grievance,
        }, default);

        contact.Classification.Should().Be(ContactClassification.Grievance);
        contact.ClassifiedBy.Should().Be(ActorId);
        contact.ClassifiedAt.Should().Be(Now);
        contact.History.Should().Contain(h => h.Action == "Classified");
    }

    [Fact]
    public async Task Update_with_nothing_to_change_does_not_write()
    {
        Exists(Stored());

        var result = await Controller().Update("contact-1",
            new UpdateContactRequest { Revision = 1 }, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Saving_the_same_details_again_does_not_bump_the_revision()
    {
        // The screen sends the whole contact on every Save. Without this, a
        // rep clicking Save twice would add a history line saying nothing
        // happened and invalidate their own next write.
        var contact = Stored();
        Exists(contact);

        var result = await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1,
            MemberId = contact.MemberId,
            Caller = new ContactCaller { Type = CallerType.Member, Name = "Ada Lovelace" },
        }, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        contact.History.Should().BeEmpty();
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_provider_name_on_its_own_is_still_a_change_worth_saving()
    {
        var contact = Stored(callerType: CallerType.Provider);
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1,
            ProviderName = "Mercy General",
        }, default);

        contact.ProviderName.Should().Be("Mercy General");
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_records_who_the_caller_turned_out_to_be()
    {
        var contact = Stored();
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1,
            MemberId = "M2",
            MemberVerification = "member ID and date of birth",
            Caller = new ContactCaller
            {
                Type = CallerType.Representative,
                Name = "Grace Hopper",
                Phone = "555-0100",
                Relationship = "daughter",
                AorOnFile = true,
            },
        }, default);

        contact.MemberId.Should().Be("M2");
        contact.MemberVerification.Should().Be("member ID and date of birth");
        contact.Caller.Type.Should().Be(CallerType.Representative);
        contact.Caller.AorOnFile.Should().BeTrue();
        contact.History.Should().ContainSingle()
               .Which.Detail.Should().Contain("member").And.Contain("verification").And.Contain("caller");
    }

    [Fact]
    public async Task Update_records_the_provider_the_call_was_about()
    {
        var contact = Stored(callerType: CallerType.Provider);
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1,
            ProviderNpi = "1234567893",
            ProviderName = "Mercy General",
        }, default);

        contact.ProviderNpi.Should().Be("1234567893");
        contact.ProviderName.Should().Be("Mercy General");
    }

    [Fact]
    public async Task Update_keeps_the_link_to_the_recording_and_the_transcript()
    {
        // The audio stays with the phone platform; CHO keeps a link to it and
        // the transcript, which is text (cho-service.md §9, question 2).
        var contact = Stored();
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1,
            RecordingLink = "https://platform.example/recordings/abc",
            RecordingPlatform = "Amazon Connect",
            TranscriptDocumentId = "doc-55",
            Summary = "Explained the denial and reissued the EOB.",
        }, default);

        contact.RecordingLink.Should().Be("https://platform.example/recordings/abc");
        contact.RecordingPlatform.Should().Be("Amazon Connect");
        contact.TranscriptDocumentId.Should().Be("doc-55");
        contact.Summary.Should().Be("Explained the denial and reissued the EOB.");
        contact.History.Should().ContainSingle()
               .Which.Detail.Should().Contain("recording").And.Contain("transcript").And.Contain("summary");
    }

    [Fact]
    public async Task Update_replaces_the_reasons_but_ignores_an_empty_list()
    {
        var contact = Stored();
        contact.ReasonCodes = ["CLAIM_STATUS"];
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1, ReasonCodes = ["ID_CARD", "BENEFITS"],
        }, default);
        contact.ReasonCodes.Should().Equal("ID_CARD", "BENEFITS");

        // An empty list is "I am not changing the reasons", not "clear them":
        // a contact closed with no reason at all is a hole in the reporting.
        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = contact.Revision, ReasonCodes = [],
        }, default);
        contact.ReasonCodes.Should().Equal("ID_CARD", "BENEFITS");
    }

    [Fact]
    public async Task Update_drops_a_reason_the_tenant_no_longer_has()
    {
        var contact = Stored();
        Exists(contact);
        SavesSuccessfully();

        await Controller().Update("contact-1", new UpdateContactRequest
        {
            Revision = 1, ReasonCodes = ["BENEFITS", "RETIRED_LAST_WEEK"],
        }, default);

        contact.ReasonCodes.Should().Equal("BENEFITS");
    }

    // ── Linking what was discussed ────────────────────────────────

    [Fact]
    public async Task Linking_a_claim_writes_a_contact_note_and_keeps_its_id()
    {
        var contact = Stored();
        Exists(contact);
        SavesSuccessfully();

        ContactNoteCommand? sent = null;
        _notes.Setup(n => n.WriteContactNoteAsync(It.IsAny<ContactNoteCommand>(), It.IsAny<CancellationToken>()))
              .Callback((ContactNoteCommand c, CancellationToken _) => sent = c)
              .ReturnsAsync("note-9");

        await Controller().LinkSubject("contact-1", new LinkSubjectRequest
        {
            Revision = 1,
            Type = ContactSubjectType.Claim,
            SubjectId = "claim-7",
            Reference = "CLM-7",
            Note = "asked why line 2 denied",
        }, default);

        sent.Should().NotBeNull();
        sent!.TenantId.Should().Be(TenantId);
        sent.SubjectId.Should().Be("claim-7");
        sent.ContactNumber.Should().Be("C-20261022-AAAA1111");

        contact.Subjects.Should().ContainSingle()
               .Which.SubjectNoteId.Should().Be("note-9");
    }

    [Fact]
    public async Task A_claim_note_that_could_not_be_written_still_leaves_the_link()
    {
        var contact = Stored();
        Exists(contact);
        SavesSuccessfully();

        // The gateway reports failure by returning null; the contact is the
        // record of the conversation and must not depend on claims-service.
        _notes.Setup(n => n.WriteContactNoteAsync(It.IsAny<ContactNoteCommand>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string?)null);

        var result = await Controller().LinkSubject("contact-1", new LinkSubjectRequest
        {
            Revision = 1, Type = ContactSubjectType.Claim, SubjectId = "claim-7",
        }, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        contact.Subjects.Should().ContainSingle()
               .Which.SubjectNoteId.Should().BeNull();
    }

    [Fact]
    public async Task Linking_the_same_subject_twice_does_not_write_a_second_note()
    {
        var contact = Stored();
        contact.Subjects.Add(new ContactSubject
        {
            Type = ContactSubjectType.Claim, SubjectId = "claim-7", LinkedAt = Now,
        });
        Exists(contact);

        var result = await Controller().LinkSubject("contact-1", new LinkSubjectRequest
        {
            Revision = 1, Type = ContactSubjectType.Claim, SubjectId = "claim-7",
        }, default);

        result.Result.Should().BeOfType<OkObjectResult>();
        contact.Subjects.Should().HaveCount(1);
        _notes.Verify(n => n.WriteContactNoteAsync(
            It.IsAny<ContactNoteCommand>(), It.IsAny<CancellationToken>()), Times.Never);
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Closing ───────────────────────────────────────────────────

    [Fact]
    public async Task Close_stamps_the_duration_and_the_resolution()
    {
        var contact = Stored();
        contact.Classification = ContactClassification.Inquiry;
        Exists(contact);
        SavesSuccessfully();

        await Controller().Close("contact-1", new CloseContactRequest
        {
            Revision = 1,
            Resolution = ContactResolution.ResolvedOnCall,
            EndedAt = Now.AddMinutes(-1),
        }, default);

        contact.Status.Should().Be(ContactStatus.Closed);
        contact.Resolution.Should().Be(ContactResolution.ResolvedOnCall);
        // Started six minutes ago, ended one minute ago: five minutes of talk
        // time, and the after-call work is the minute since.
        contact.DurationSeconds.Should().Be(300);
        contact.ClosedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Close_refuses_an_end_before_the_start()
    {
        Exists(Stored());

        var result = await Controller().Close("contact-1", new CloseContactRequest
        {
            Revision = 1,
            Resolution = ContactResolution.ResolvedOnCall,
            EndedAt = Now.AddHours(-2), Classification = ContactClassification.Inquiry,
        }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _contacts.Verify(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_refuses_a_contact_that_is_already_closed()
    {
        Exists(Stored(ContactStatus.Closed));

        var result = await Controller().Close("contact-1", new CloseContactRequest
        {
            Revision = 1, Resolution = ContactResolution.ResolvedOnCall, Classification = ContactClassification.Inquiry,
        }, default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Reopen_leaves_the_close_in_the_history()
    {
        var contact = Stored(ContactStatus.Closed);
        contact.ClosedAt = Now.AddMinutes(-2);
        contact.History.Add(new ContactRevisionEntry { Action = "Closed", At = Now.AddMinutes(-2), By = ActorId });
        Exists(contact);
        SavesSuccessfully();

        await Controller().Reopen("contact-1",
            new ReopenContactRequest { Revision = 1, Reason = "caller rang back" }, default);

        contact.Status.Should().Be(ContactStatus.Open);
        contact.ClosedAt.Should().BeNull();
        contact.History.Select(h => h.Action).Should().Equal("Closed", "Reopened");
    }

    [Fact]
    public async Task A_competing_write_is_reported_as_a_conflict()
    {
        var contact = Stored();
        Exists(contact);
        _contacts.Setup(c => c.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new ContactConcurrencyException("contact-1", 1));

        var result = await Controller().Update("contact-1",
            new UpdateContactRequest { Revision = 1, Summary = "wrap-up" }, default);

        result.Result.Should().BeOfType<ConflictObjectResult>();
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
