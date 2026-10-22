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
            EndedAt = Now.AddHours(-2),
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
            Revision = 1, Resolution = ContactResolution.ResolvedOnCall,
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
