using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>CMS-0057-F decision timeframe on the authorization record: the extension and the member's extension notice.</summary>
public class AuthorizationDecisionTimeframeTests : TestContext
{
    private readonly Mock<IAuthorizationService> _auths = new();
    private readonly Mock<ICorrespondenceService> _letters = new();
    private readonly Mock<IMemberService> _members = new();
    private readonly Mock<IUserContextService> _user = new();
    private AuthorizationDetails? _changed;

    public AuthorizationDecisionTimeframeTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_auths.Object);
        Services.AddSingleton(_letters.Object);
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.HasPermission("authorizations:decide")).Returns(true);
        _members.Setup(m => m.GetMemberByIdAsync("M1")).ReturnsAsync(new MemberDetails
        {
            MemberId = "M1", FirstName = "Ana", LastName = "Reyes", Address = "1 Synthetic Way", City = "Austin", State = "TX", ZipCode = "78701",
        });
    }

    private static AuthorizationDetails Open(string lob = "Medicaid") => new()
    {
        Id = "auth-1", AuthorizationNumber = "PA-77", MemberId = "M1", Status = 2, LineOfBusiness = lob,
        DecisionDueAt = new DateTime(2027, 1, 20, 12, 0, 0, DateTimeKind.Utc),
    };

    private static AuthorizationDetails Extended(bool noticed = false)
    {
        var a = Open();
        a.DecisionExtension = new DecisionExtensionView
        {
            GrantedAt = new DateTime(2027, 1, 15, 9, 0, 0, DateTimeKind.Utc), GrantedBy = "reviewer-1",
            OriginalDueAt = a.DecisionDueAt!.Value, ExtendedDueAt = a.DecisionDueAt.Value.AddDays(14),
            Basis = "PlanNeedsAdditionalInformation", Reason = "Waiting for imaging from the provider",
            MemberNotice = noticed ? new ExtensionMemberNoticeView { LetterId = "LTR-1", RecordedBy = "reviewer-1" } : null,
        };
        a.DecisionDueAt = a.DecisionExtension.ExtendedDueAt;
        return a;
    }

    private IRenderedComponent<AuthorizationDecisionTimeframePanel> Render(AuthorizationDetails a) =>
        RenderComponent<AuthorizationDecisionTimeframePanel>(p => p
            .Add(x => x.Authorization, a).Add(x => x.OnChanged, (AuthorizationDetails u) => _changed = u));

    [Fact]
    public async Task An_open_medicaid_request_can_be_extended_with_a_ground_and_reason()
    {
        var extended = Extended();
        _auths.Setup(s => s.ExtendDecisionDeadlineAsync("auth-1", "ProviderRequested", "Provider is sending records", null))
            .ReturnsAsync(new AuthorizationActionResult(extended, null));
        var cut = Render(Open());

        var button = cut.Find("[data-testid=extend-timeframe]");
        button.HasAttribute("disabled").Should().BeTrue("a ground and a reason are required");
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync("ProviderRequested"));
        cut.Find("textarea").Change("Provider is sending records");
        cut.Find("[data-testid=extend-timeframe]").Click();

        cut.WaitForAssertion(() => _changed.Should().BeSameAs(extended));
    }

    [Fact]
    public void A_commercial_request_cannot_be_extended()
    {
        var cut = Render(Open("Commercial"));
        cut.FindAll("[data-testid=extend-timeframe]").Should().BeEmpty();
        cut.Markup.Should().Contain("Only Medicare Advantage and Medicaid");
    }

    [Fact]
    public void Without_decide_permission_nothing_can_be_changed()
    {
        _user.Setup(u => u.HasPermission("authorizations:decide")).Returns(false);
        Render(Open()).FindAll("[data-testid=extend-timeframe]").Should().BeEmpty();
        Render(Extended()).FindAll("[data-testid=send-extension-notice]").Should().BeEmpty();
    }

    [Fact]
    public void Service_refusal_is_shown()
    {
        _auths.Setup(s => s.ExtendDecisionDeadlineAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), null))
            .ReturnsAsync(new AuthorizationActionResult(null, "The decision deadline has already passed."));
        var cut = Render(Open());
        cut.InvokeAsync(() => cut.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync("EnrolleeRequested"));
        cut.Find("textarea").Change("Member asked");
        cut.Find("[data-testid=extend-timeframe]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=timeframe-error]").TextContent.Should().Contain("already passed"));
        _changed.Should().BeNull();
    }

    [Fact]
    public void An_owed_notice_is_sent_to_the_member_address_and_recorded()
    {
        CorrespondenceLetterRequest? sent = null;
        _letters.Setup(l => l.RequestLetterAsync(It.IsAny<CorrespondenceLetterRequest>()))
            .Callback<CorrespondenceLetterRequest>(r => sent = r)
            .ReturnsAsync(new LetterRequestResult(new CorrespondenceLetter { Id = "LTR-9" }, null));
        var noticed = Extended(noticed: true);
        _auths.Setup(s => s.RecordExtensionNoticeAsync("auth-1", "LTR-9")).ReturnsAsync(new AuthorizationActionResult(noticed, null));
        var cut = Render(Extended());

        cut.Find("[data-testid=extension-notice]").TextContent.Should().Contain("a grievance");
        cut.Find("[data-testid=send-extension-notice]").Click();

        cut.WaitForAssertion(() => _changed.Should().BeSameAs(noticed));
        sent!.LetterTypeCode.Should().Be("PA-DECISION-EXTENSION");
        sent.Subjects.Select(s => (s.Kind, s.Id)).Should().Equal(("Authorization", "auth-1"), ("Member", "M1"));
        sent.Recipient.AddressLine1.Should().Be("1 Synthetic Way");
        sent.Recipient.AddressSource.Should().Be("MemberRecord");
        sent.Data!["reason"]!.GetValue<string>().Should().Be("Waiting for imaging from the provider");
        sent.Data["grievanceRight"]!.GetValue<string>().Should().Be("Grievance");
    }

    [Fact]
    public void No_letter_is_requested_without_a_complete_member_address()
    {
        _members.Setup(m => m.GetMemberByIdAsync("M1")).ReturnsAsync(new MemberDetails { MemberId = "M1", FirstName = "Ana" });
        var cut = Render(Extended());
        cut.Find("[data-testid=send-extension-notice]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=timeframe-error]").TextContent.Should().Contain("mailing address"));
        _letters.Verify(l => l.RequestLetterAsync(It.IsAny<CorrespondenceLetterRequest>()), Times.Never);
    }

    [Fact]
    public void A_missing_template_is_reported_and_nothing_is_recorded()
    {
        _letters.Setup(l => l.RequestLetterAsync(It.IsAny<CorrespondenceLetterRequest>()))
            .ReturnsAsync(new LetterRequestResult(null, "No approved template applies."));
        var cut = Render(Extended());
        cut.Find("[data-testid=send-extension-notice]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=timeframe-error]").TextContent.Should().Contain("No approved template"));
        _auths.Verify(s => s.RecordExtensionNoticeAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void A_recorded_notice_is_shown()
    {
        var cut = Render(Extended(noticed: true));
        cut.Find("[data-testid=extension-notice]").TextContent.Should().Contain("LTR-1");
        cut.FindAll("[data-testid=send-extension-notice]").Should().BeEmpty();
        cut.Find("[data-testid=extension-summary]").TextContent.Should().Contain("plan needs additional information");
    }
}
