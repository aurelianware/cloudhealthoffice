using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Record search always needs criteria, in every tenant (portal-search.md §3.5).
/// There is no demo or configuration switch that lists records on page load.
/// </summary>
public class SearchCriteriaRequiredTests : TestContext
{
    private readonly Mock<IMemberService> _members = new();
    private readonly Mock<IAuthorizationService> _auths = new();

    public SearchCriteriaRequiredTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(Mock.Of<ICoverageService>());
        Services.AddSingleton(_auths.Object);
        var user = new Mock<IUserContextService>();
        user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "u1", Roles = ["MemberServices"] });
        user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns(true);
        Services.AddSingleton(user.Object);
    }

    [Fact]
    public void Members_page_does_not_list_members_on_load()
    {
        var cut = RenderComponent<Members>();

        _members.Verify(m => m.SearchMembersAsync(It.IsAny<string>()), Times.Never);
        cut.Markup.Should().NotContain("No members found");
    }

    [Fact]
    public void Members_search_without_criteria_asks_for_one()
    {
        var cut = RenderComponent<Members>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Search")).Click();

        cut.Markup.Should().Contain("Please enter at least one search criterion");
        _members.Verify(m => m.SearchMembersAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Authorizations_page_does_not_list_authorizations_on_load()
    {
        RenderComponent<Authorizations>();

        _auths.Verify(a => a.GetAuthorizationsAsync(It.IsAny<string?>()), Times.Never);
    }
}
