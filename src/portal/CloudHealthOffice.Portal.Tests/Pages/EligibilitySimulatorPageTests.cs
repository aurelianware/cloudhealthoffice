using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// bUnit tests for the 270/271 Eligibility Simulator (/eligibility): the
/// X12 270 search options and the portal-side name + DOB resolution.
/// </summary>
public class EligibilitySimulatorPageTests : TestContext
{
    private static readonly DateTime Dob = new(1980, 3, 14);
    private readonly Mock<IEligibilityService> _eligibility = new();
    private readonly Mock<IMemberService> _members = new();
    private readonly List<object> _sent = new();

    public EligibilitySimulatorPageTests()
    {
        _eligibility.Setup(e => e.CheckEligibilityAsync(It.IsAny<object>()))
            .Callback<object>(_sent.Add)
            .ReturnsAsync(new EligibilityResponse { IsCovered = true, InsurancePlanName = "Gold PPO" });
        Services.AddSingleton(_eligibility.Object);
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(GatingUsers.With("eligibility:check").Object);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<Eligibility> RenderPage()
    {
        var cut = RenderComponent<Eligibility>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("270/271 Eligibility Simulator"));
        return cut;
    }

    private static void Type(IRenderedComponent<Eligibility> cut, string testId, string value)
        => cut.Find($"input[data-testid='{testId}']").Input(value);

    private static void TypeDob(IRenderedComponent<Eligibility> cut, string value)
        => cut.Find("input[data-testid='dob']").Change(value);

    private void Send(IRenderedComponent<Eligibility> cut)
        => cut.Find("[data-testid='send-270']").Click();

    private static string Sent(object body, string key)
        => ((Dictionary<string, object?>)body)[key]?.ToString() ?? string.Empty;

    [Fact]
    public void Page_IsTitledAsSimulator_AndExplainsPurpose()
    {
        var cut = RenderPage();

        cut.Markup.Should().Contain("what a provider or clearinghouse would receive in a 271");
        cut.Markup.Should().Contain("trading-partner testing");
    }

    [Fact]
    public void IdOnly_SendsInquiryWithoutMemberSearch()
    {
        var cut = RenderPage();
        Type(cut, "subscriber-id", "MBR-001");

        Send(cut);

        cut.WaitForAssertion(() => _sent.Should().ContainSingle());
        Sent(_sent[0], "subscriberId").Should().Be("MBR-001");
        _members.Verify(m => m.SearchMembersAsync(It.IsAny<string>()), Times.Never);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("ACTIVE COVERAGE"));
    }

    [Fact]
    public void NameWithoutDob_IsRejected_AndNothingIsSent()
    {
        var cut = RenderPage();
        Type(cut, "last-name", "Diaz");
        Type(cut, "first-name", "Ana");

        Send(cut);

        cut.WaitForAssertion(() => cut.Find("[data-testid='criteria-error']").TextContent
            .Should().Contain("Last Name + First Name + Date of Birth"));
        _eligibility.Verify(e => e.CheckEligibilityAsync(It.IsAny<object>()), Times.Never);
        _members.Verify(m => m.SearchMembersAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void NameAndDob_ResolvesMemberThenSendsInquiryWithResolvedId()
    {
        _members.Setup(m => m.SearchMembersAsync("Diaz")).ReturnsAsync(new List<MemberSummary>
        {
            new() { MemberId = "MBR-777", FirstName = "Ana", LastName = "Diaz", DateOfBirth = Dob },
            new() { MemberId = "MBR-888", FirstName = "Ana", LastName = "Diaz", DateOfBirth = Dob.AddYears(-30) },
        });
        var cut = RenderPage();
        Type(cut, "last-name", "Diaz");
        Type(cut, "first-name", "Ana");
        TypeDob(cut, "03/14/1980");

        Send(cut);

        cut.WaitForAssertion(() => _sent.Should().ContainSingle());
        Sent(_sent[0], "subscriberId").Should().Be("MBR-777");
        Sent(_sent[0], "subscriberLastName").Should().Be("Diaz");
        ((Dictionary<string, object?>)_sent[0])["subscriberDOB"].Should().Be(Dob);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Gold PPO"));
    }

    [Fact]
    public void NameAndDob_NoMatch_ShowsSubscriberNotFoundReject()
    {
        _members.Setup(m => m.SearchMembersAsync(It.IsAny<string>())).ReturnsAsync(new List<MemberSummary>());
        var cut = RenderPage();
        Type(cut, "last-name", "Nobody");
        Type(cut, "first-name", "Ana");
        TypeDob(cut, "03/14/1980");

        Send(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Subscriber/Insured Not Found"));
        cut.Markup.Should().Contain("NO ACTIVE COVERAGE");
        _eligibility.Verify(e => e.CheckEligibilityAsync(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public void NameAndDob_MultipleMatches_AsksUserToPick_ThenSendsPickedId()
    {
        _members.Setup(m => m.SearchMembersAsync("Diaz")).ReturnsAsync(new List<MemberSummary>
        {
            new() { MemberId = "MBR-1", FirstName = "Ana", LastName = "Diaz", DateOfBirth = Dob },
            new() { MemberId = "MBR-2", FirstName = "Ana", LastName = "Diaz", DateOfBirth = Dob },
        });
        var cut = RenderPage();
        Type(cut, "last-name", "Diaz");
        Type(cut, "first-name", "Ana");
        TypeDob(cut, "03/14/1980");

        Send(cut);

        cut.WaitForAssertion(() => cut.Find("[data-testid='ambiguous-matches']"));
        _eligibility.Verify(e => e.CheckEligibilityAsync(It.IsAny<object>()), Times.Never);

        cut.Find("[data-testid='candidate-MBR-2']").Click();

        cut.WaitForAssertion(() => _sent.Should().ContainSingle());
        Sent(_sent[0], "subscriberId").Should().Be("MBR-2");
    }
}
