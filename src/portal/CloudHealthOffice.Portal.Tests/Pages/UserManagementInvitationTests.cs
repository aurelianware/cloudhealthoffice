using System.Net;
using System.Text;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>The user management page's invitation list and user actions.</summary>
public class UserManagementInvitationTests : TestContext
{
    private readonly Mock<ITenantInvitationService> _invitations = new();
    private readonly List<string> _requestedUrls = new();

    public UserManagementInvitationTests()
    {
        var users = new Mock<IUserContextService>();
        users.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "admin", TenantId = "tenant-a" });
        users.Setup(u => u.HasPermission("users:manage")).Returns(true);
        var tenant = new Mock<ITenantContextService>();
        tenant.Setup(t => t.GetCurrentTenantContextAsync()).ReturnsAsync(new TenantContext { TenantId = "tenant-a" });

        var handler = new FakeHandler(request =>
        {
            _requestedUrls.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [
                      {"id":"u-active","email":"a@acme.example","displayName":"Active Linked","roles":["ClaimsExaminer"],"status":"Active","azureAdObjectId":"oid-1"},
                      {"id":"u-invited","email":"g@partner.example","displayName":"Invited Guest","roles":["ClaimsExaminer"],"status":"Invited","azureAdObjectId":""}
                    ]
                    """, Encoding.UTF8, "application/json"),
            };
        });

        _invitations.Setup(i => i.ListAsync("tenant-a", It.IsAny<CancellationToken>())).ReturnsAsync(new List<InvitationItem>
        {
            new() { Id = "inv-1", Email = "g@partner.example", DisplayName = "Invited Guest", Status = "Pending", Roles = { "ClaimsExaminer" } },
            new() { Id = "inv-2", Email = "old@partner.example", Status = "Redeemed" },
        });

        Services.AddSingleton(users.Object);
        Services.AddSingleton(tenant.Object);
        Services.AddSingleton(_invitations.Object);
        Services.AddSingleton(new HttpClient(handler));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:TenantService"] = "http://tenant-service.test/api" })
            .Build());
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void ListsTheTenantsInvitations_WithResendAndRevokeOnlyWhileOpen()
    {
        var cut = RenderComponent<UserManagement>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=invitations]"));
        _invitations.Verify(i => i.ListAsync("tenant-a", It.IsAny<CancellationToken>()), Times.Once);
        _requestedUrls.Should().Contain("http://tenant-service.test/api/v1/tenants/tenant-a/users");

        var rows = cut.Find("[data-testid=invitations]").QuerySelectorAll("tbody tr");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("Pending");
        rows[0].QuerySelectorAll("button").Should().HaveCount(2, "a pending invitation can be resent or revoked");
        rows[1].TextContent.Should().Contain("Redeemed");
        rows[1].QuerySelectorAll("button").Should().BeEmpty();
    }

    [Fact]
    public void OffersInviteAndExplainsThatAddIsForTheOwnDirectoryOnly()
    {
        var cut = RenderComponent<UserManagement>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=invite-user]"));
        cut.Find("[data-testid=add-directory-user]").TextContent.Should().Contain("Your Directory");
        cut.Markup.Should().Contain("own Microsoft Entra directory");
    }

    [Fact]
    public void InvitedUsersCannotBeEnabledByHand_AndLinkedUsersCanBeUnlinked()
    {
        var cut = RenderComponent<UserManagement>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=invitations]"));
        var userRows = cut.FindAll("table").First().QuerySelectorAll("tbody tr");
        var active = userRows.Single(r => r.TextContent.Contains("Active Linked"));
        var invited = userRows.Single(r => r.TextContent.Contains("Invited Guest"));

        // Active + linked: edit, disable, unlink.
        active.QuerySelectorAll("button").Should().HaveCount(3);
        // Invited: edit only (no enable; activation is by redemption, and there is no link to remove).
        invited.QuerySelectorAll("button").Should().HaveCount(1);
    }
}
