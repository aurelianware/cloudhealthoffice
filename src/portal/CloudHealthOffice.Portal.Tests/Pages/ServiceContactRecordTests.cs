using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The contact screen: what the rep sees while the caller is on the line,
/// and what it does when a write is refused.
/// </summary>
public class ServiceContactRecordTests : TestContext
{
    private readonly Mock<IContactTrackingService> _contacts = new();
    private readonly Mock<IUserContextService> _user = new();

    public ServiceContactRecordTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_contacts.Object);
        Services.AddSingleton(Mock.Of<IWorkManagementService>());
        Services.AddSingleton(Mock.Of<IDocumentCatalogService>(d => d.GetForSubjectAsync(
            It.IsAny<DocumentSubjectKind>(), It.IsAny<string>(), It.IsAny<CancellationToken>()) == Task.FromResult(DocumentSubjectListing.Empty)));
        Services.AddSingleton(_user.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _user.Setup(u => u.GetCurrentUserAsync())
             .ReturnsAsync(new UserContext { UserId = "rep-1", Roles = ["MemberServices"] });
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);

        _contacts.Setup(c => c.GetReasonsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(
                 [
                     new ContactReasonOption { Code = "CLAIM_STATUS", Label = "Claim status", Category = "Claims" },
                     new ContactReasonOption { Code = "ID_CARD", Label = "ID card", Category = "Enrollment" },
                 ]);
    }

    private static ContactRecord Contact(
        ContactStatus status = ContactStatus.Open,
        ContactClassification? classification = null) => new()
    {
        Id = "contact-1",
        ContactNumber = "C-20261022-AAAA1111",
        Revision = 3,
        Status = status,
        Channel = ContactChannel.Phone,
        Direction = ContactDirection.Inbound,
        Caller = new ContactCaller { Type = CallerType.Member, Name = "Ada Lovelace" },
        MemberId = "M1",
        StartedAt = new DateTime(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc),
        AgentId = "rep-1",
        Classification = classification,
        ReasonCodes = ["CLAIM_STATUS"],
        History = [new ContactHistoryEntry
        {
            Action = "Started", At = new DateTime(2026, 10, 22, 14, 0, 0, DateTimeKind.Utc), By = "rep-1",
        }],
    };

    private void Loads(ContactRecord contact) =>
        _contacts.Setup(c => c.GetAsync("contact-1", It.IsAny<CancellationToken>())).ReturnsAsync(contact);

    private IRenderedComponent<ServiceContactRecord> Render() =>
        RenderComponent<ServiceContactRecord>(p => p.Add(x => x.ContactId, "contact-1"));

    [Fact]
    public void Shows_the_contact_number_and_that_it_is_open()
    {
        Loads(Contact());

        var page = Render();

        page.Markup.Should().Contain("C-20261022-AAAA1111");
        page.Find("[data-testid=contact-status]").TextContent.Should().Contain("Open");
    }

    [Fact]
    public void A_closed_contact_shows_its_talk_time_rather_than_the_close_controls()
    {
        var closed = Contact(ContactStatus.Closed);
        closed.DurationSeconds = 272;
        closed.Resolution = ContactResolution.ResolvedOnCall;
        Loads(closed);

        var page = Render();

        page.Markup.Should().Contain("4m 32s");
        page.Markup.Should().Contain("Resolved on the call");
        page.FindAll("[data-testid=close-contact]").Should().BeEmpty();
        page.FindAll("[data-testid=reopen-contact]").Should().ContainSingle();
    }

    [Fact]
    public void A_grievance_offers_explicit_case_intake_without_claiming_it_is_already_open()
    {
        // Step 1 records the classification but opens no case and starts no
        // clock, so the screen must not imply one is running.
        Loads(Contact(classification: ContactClassification.Grievance));

        var page = Render();

        page.Markup.Should().Contain("Starts member grievance intake");
        page.Find("[data-testid=open-case]").Should().NotBeNull();
    }

    [Fact]
    public void An_inquiry_claims_no_consequence()
    {
        Loads(Contact(classification: ContactClassification.Inquiry));

        Render().Markup.Should().NotContain("Cases are not opened from a contact yet");
    }

    [Fact]
    public void Linking_a_claim_sends_the_revision_it_read()
    {
        Loads(Contact());

        LinkContactSubjectRequest? sent = null;
        _contacts.Setup(c => c.LinkSubjectAsync("contact-1", It.IsAny<LinkContactSubjectRequest>(), It.IsAny<CancellationToken>()))
                 .Callback((string _, LinkContactSubjectRequest r, CancellationToken _) => sent = r)
                 .ReturnsAsync(Contact());

        var page = Render();
        page.Find("input[data-testid=subject-id]").Change("claim-7");
        page.Find("[data-testid=link-subject]").Click();

        sent.Should().NotBeNull();
        sent!.Revision.Should().Be(3);
        sent.SubjectId.Should().Be("claim-7");
        sent.Type.Should().Be(ContactSubjectType.Claim);
    }

    [Fact]
    public void A_linked_claim_whose_note_failed_is_marked_rather_than_hidden()
    {
        var contact = Contact();
        contact.Subjects =
        [
            new ContactSubject { Type = ContactSubjectType.Claim, SubjectId = "claim-7", Reference = "CLM-7" },
        ];
        Loads(contact);

        var page = Render();

        page.Markup.Should().Contain("CLM-7");
        page.Markup.Should().Contain("the claim's own history does not show the call");
    }

    [Fact]
    public void A_refused_write_shows_the_services_own_sentence()
    {
        Loads(Contact());
        _contacts.Setup(c => c.UpdateAsync("contact-1", It.IsAny<UpdateContactRequest>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new ContactWriteRefusedException(
                     "This contact has changed since you read it (it is now revision 4). Reload it and try again."));

        var page = Render();
        page.Find("[data-testid=save-contact]").Click();

        page.Markup.Should().Contain("it is now revision 4");
    }

    [Fact]
    public void A_refused_write_re_reads_the_version_that_won()
    {
        Loads(Contact());
        _contacts.Setup(c => c.UpdateAsync("contact-1", It.IsAny<UpdateContactRequest>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new ContactWriteRefusedException("changed under you"));

        var page = Render();
        page.Find("[data-testid=save-contact]").Click();

        // Once on load, once after the refusal: the rep sees what actually
        // stands before deciding what to retype.
        _contacts.Verify(c => c.GetAsync("contact-1", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void A_contact_service_that_is_down_says_so_instead_of_rendering_an_empty_screen()
    {
        _contacts.Setup(c => c.GetAsync("contact-1", It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new ServiceUnavailableException("Contact Service"));

        Render().Markup.Should().Contain("Contact Service");
    }
}
