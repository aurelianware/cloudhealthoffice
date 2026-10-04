using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>A user holding exactly the given permissions.</summary>
internal static class GatingUsers
{
    public static Mock<IUserContextService> With(params string[] permissions)
    {
        var granted = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
        var users = new Mock<IUserContextService>();
        users.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext
        {
            UserId = "u-1",
            TenantId = "tenant-a",
            Roles = new List<string> { "SomeRole" },
            Permissions = granted
        });
        users.Setup(u => u.HasPermission(It.IsAny<string>())).Returns((string p) => granted.Contains(p));
        return users;
    }
}

/// <summary>
/// The provider contracts page shows write actions only to contracts:write
/// holders. Finance and ComplianceOfficer read contracts but every write
/// returns 403 from provider-contracts-service.
/// </summary>
public class ProviderContractsPageGatingTests : TestContext
{
    private readonly Mock<IProviderContractsService> _contracts = new();

    private static readonly string[] WriteButtons =
    [
        "new-contract", "edit-contract", "activate-contract", "suspend-contract", "terminate-contract"
    ];

    public ProviderContractsPageGatingTests()
    {
        _contracts.Setup(c => c.GetContractsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<ProviderContractSummary>
            {
                new() { Id = "c-draft", ContractNumber = "CTR-1", ProviderName = "Dr. Draft", Status = "Draft" },
                new() { Id = "c-active", ContractNumber = "CTR-2", ProviderName = "Dr. Active", Status = "Active" },
            });
        Services.AddSingleton(_contracts.Object);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ProviderContracts> Render(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions).Object);
        var cut = RenderComponent<ProviderContracts>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Dr. Active"));
        return cut;
    }

    [Fact]
    public void ReadOnlyHolder_SeesContracts_ButNoWriteActions()
    {
        // Before: every contracts:read holder saw New / Edit / Activate /
        // Suspend / Terminate, each of which the service refuses with 403.
        var cut = Render("contracts:read");

        cut.Markup.Should().NotContain("New Contract");
        var rows = cut.FindAll("tbody tr").Where(r => r.TextContent.Contains("Dr.")).ToList();
        rows.Should().HaveCount(2);
        foreach (var row in rows)
            row.QuerySelectorAll("button").Should().HaveCount(1, "only View Details is offered");
        foreach (var button in WriteButtons)
            cut.FindAll($"[data-testid={button}]").Should().BeEmpty(button);
    }

    [Fact]
    public void ContractsWriter_SeesWriteActions()
    {
        var cut = Render("contracts:read", "contracts:write");

        foreach (var button in WriteButtons)
            cut.FindAll($"[data-testid={button}]").Should().NotBeEmpty(button);
    }
}

/// <summary>
/// The capitation rate-config page loads contract data, so it needs
/// contracts:read; billing:read alone (FinanceApprover) does not open it.
/// </summary>
public class CapitationContractsPageGatingTests : TestContext
{
    public CapitationContractsPageGatingTests()
    {
        var capitation = new Mock<ICapitationService>();
        capitation.Setup(c => c.GetContractsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new List<CapitationContractSummary>());
        Services.AddSingleton(capitation.Object);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void BillingReadOnly_IsDenied()
    {
        // Before: the gate also accepted billing:read, so FinanceApprover
        // opened a page whose data it cannot load.
        Services.AddSingleton(GatingUsers.With("billing:read", "payments:read").Object);

        var cut = RenderComponent<CapitationContracts>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("don't have access"));
        cut.Markup.Should().NotContain("Per-contract PMPM rate tiers");
    }

    [Fact]
    public void ContractsReader_SeesThePage()
    {
        Services.AddSingleton(GatingUsers.With("contracts:read").Object);

        var cut = RenderComponent<CapitationContracts>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Per-contract PMPM rate tiers"));
        cut.Markup.Should().NotContain("don't have access");
    }
}

/// <summary>The navigation shows the Sponsors and capitation Rate Config links only to roles that can read them.</summary>
public class MainLayoutNavGatingTests : TestContext
{
    public MainLayoutNavGatingTests()
    {
        var tenant = new Mock<ITenantContextService>();
        tenant.Setup(t => t.GetCurrentTenantContextAsync()).ReturnsAsync(new TenantContext { TenantId = "tenant-a", TenantName = "Tenant A" });
        tenant.Setup(t => t.GetAvailableTenantsAsync()).ReturnsAsync(new List<TenantSubscription>());
        Services.AddSingleton(tenant.Object);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.AddTestAuthorization().SetAuthorized("user@test.example");
    }

    private IRenderedComponent<MainLayout> Render(params string[] permissions)
    {
        Services.AddSingleton(GatingUsers.With(permissions).Object);
        var cut = RenderComponent<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "body"))));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Benefit Plans"));
        return cut;
    }

    private static bool Links(IRenderedComponent<MainLayout> cut, string href)
        => cut.FindAll($"a[href='{href}']").Count > 0;

    [Fact]
    public void WithoutPermissions_SponsorsAndRateConfigLinksAreHidden()
    {
        // Before: both links were shown to every signed-in user.
        var cut = Render("billing:read", "payments:read");

        Links(cut, "/sponsors").Should().BeFalse();
        Links(cut, "/capitation/rate-config").Should().BeFalse();
        Links(cut, "/benefit-plans").Should().BeTrue("ungated links still render");
    }

    [Fact]
    public void WithPermissions_SponsorsAndRateConfigLinksAreShown()
    {
        var cut = Render("enrollment:read", "contracts:read");

        Links(cut, "/sponsors").Should().BeTrue();
        Links(cut, "/capitation/rate-config").Should().BeTrue();
    }
}
