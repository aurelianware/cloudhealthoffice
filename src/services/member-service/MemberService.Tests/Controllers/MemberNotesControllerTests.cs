using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Controllers;

public class MemberNotesControllerTests
{
    private const string Tenant = "tenant-test";
    private const string MemberId = "M-001";

    private static (MemberNotesController ctl,
                    InMemoryMemberRepository members,
                    InMemoryMemberNoteRepository notes,
                    InMemoryMemberEventRepository events) Build()
    {
        var members = new InMemoryMemberRepository();
        members.Members.Add(new Member
        {
            TenantId = Tenant,
            MemberId = MemberId,
            FirstName = "Alice",
            LastName = "Example",
            DateOfBirth = new DateTime(1985, 6, 15),
            EffectiveDate = new DateTime(2024, 1, 1),
            GroupNumber = "GRP",
            IsSubscriber = true
        });
        var notes = new InMemoryMemberNoteRepository();
        var events = new InMemoryMemberEventRepository();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);

        var ctl = new MemberNotesController(members, notes, publisher);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, members, notes, events);
    }

    private static CreateMemberNoteRequest Req(
        MemberNoteCategory cat = MemberNoteCategory.CustomerService,
        string subject = "Inbound call",
        string body = "Member called about EOB",
        string? linkedType = null,
        string? linkedId = null) => new()
        {
            Category = cat,
            Subject = subject,
            Body = body,
            LinkedResourceType = linkedType,
            LinkedResourceId = linkedId
        };

    [Fact]
    public async Task CreateNote_PersistsAndEmitsAuditEvent()
    {
        var (ctl, _, notes, events) = Build();

        var resp = await ctl.CreateNote(MemberId, Req(), CancellationToken.None);
        resp.Should().BeOfType<CreatedAtActionResult>();

        notes.Notes.Should().ContainSingle();
        var stored = notes.Notes[0];
        stored.Category.Should().Be(MemberNoteCategory.CustomerService);
        stored.Author.Should().NotBeNullOrEmpty();

        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberNoteCreated);
    }

    [Fact]
    public async Task CreateNote_UnknownMember_Returns404()
    {
        var (ctl, _, _, _) = Build();
        var resp = await ctl.CreateNote("NOPE", Req(), CancellationToken.None);
        resp.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListNotes_FilterByCategory_OnlyMatching()
    {
        var (ctl, _, _, _) = Build();
        await ctl.CreateNote(MemberId, Req(MemberNoteCategory.CustomerService), CancellationToken.None);
        await ctl.CreateNote(MemberId, Req(MemberNoteCategory.Appeals,  "Appeal filed"), CancellationToken.None);
        await ctl.CreateNote(MemberId, Req(MemberNoteCategory.Billing, "Premium concern"), CancellationToken.None);

        var resp = await ctl.ListNotes(MemberId, category: MemberNoteCategory.Appeals,
            pageSize: 20, continuationToken: null, ct: CancellationToken.None);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<MemberNoteListResponse>().Subject;
        body.Items.Should().ContainSingle(n => n.Category == MemberNoteCategory.Appeals);
    }

    [Fact]
    public async Task ListNotes_NewestFirst()
    {
        var (ctl, _, notes, _) = Build();
        // Insert with explicit dates so the ordering check is deterministic
        notes.Notes.Add(new MemberNote
        {
            TenantId = Tenant, MemberId = MemberId, Id = "n1",
            Category = MemberNoteCategory.CustomerService, Subject = "old",
            Body = "old", Author = "csr", CreatedDate = DateTime.UtcNow.AddDays(-2)
        });
        notes.Notes.Add(new MemberNote
        {
            TenantId = Tenant, MemberId = MemberId, Id = "n2",
            Category = MemberNoteCategory.CustomerService, Subject = "new",
            Body = "new", Author = "csr", CreatedDate = DateTime.UtcNow
        });

        var resp = await ctl.ListNotes(MemberId, null, 20, null, CancellationToken.None);
        var body = ((OkObjectResult)resp).Value.Should().BeOfType<MemberNoteListResponse>().Subject;
        body.Items[0].Id.Should().Be("n2");
    }

    [Fact]
    public async Task ListNotes_EmitsViewedAuditEvent()
    {
        var (ctl, _, _, events) = Build();
        await ctl.CreateNote(MemberId, Req(), CancellationToken.None);
        var baseline = events.All.Count;

        await ctl.ListNotes(MemberId, null, 20, null, CancellationToken.None);
        events.All.Count.Should().BeGreaterThan(baseline);
        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberNoteViewed);
    }

    [Fact]
    public async Task GetNote_EmitsViewedAuditEvent()
    {
        var (ctl, _, _, events) = Build();
        var created = (CreatedAtActionResult)await ctl.CreateNote(MemberId, Req(), CancellationToken.None);
        var note = (MemberNote)created.Value!;

        await ctl.GetNote(MemberId, note.Id, CancellationToken.None);
        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberNoteViewed);
    }

    [Fact]
    public void NoteRepository_HasNoUpdateOrDeleteMethod_EnforcesImmutability()
    {
        // Reflection-based contract test: catching anyone who later adds an
        // Update / Delete method that would break the immutability invariant.
        var iface = typeof(MemberService.Repositories.IMemberNoteRepository);
        var methodNames = iface.GetMethods().Select(m => m.Name).ToList();
        methodNames.Should().NotContain("UpdateAsync");
        methodNames.Should().NotContain("DeleteAsync");
    }

    [Fact]
    public async Task CreateNote_AsCorrection_LinksBackToOriginal()
    {
        var (ctl, _, notes, _) = Build();
        var created = (CreatedAtActionResult)await ctl.CreateNote(MemberId,
            Req(subject: "Original (typo)"), CancellationToken.None);
        var original = (MemberNote)created.Value!;

        var correction = (CreatedAtActionResult)await ctl.CreateNote(MemberId,
            Req(subject: "Correction", linkedType: "MemberNote", linkedId: original.Id),
            CancellationToken.None);
        var corrNote = (MemberNote)correction.Value!;

        corrNote.LinkedResourceType.Should().Be("MemberNote");
        corrNote.LinkedResourceId.Should().Be(original.Id);
        notes.Notes.Should().HaveCount(2, "correction is a new note, not an edit");
    }
}
