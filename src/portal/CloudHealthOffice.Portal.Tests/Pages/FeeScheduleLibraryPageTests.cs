using System.Net;
using System.Text.Json.Nodes;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using PortalFeeScheduleLibraryService = CloudHealthOffice.Portal.Services.FeeScheduleLibraryService;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Guided fee schedule onboarding: browse the shared library, open a
/// schedule's rates, preview at a percentage and deploy as a plan default.
/// </summary>
public class FeeScheduleLibraryPageTests : TestContext
{
    private const string MedicareId = "lib:Medicare PFS 2026 TX DALLAS (18):20260101";
    private readonly Mock<IFeeScheduleLibraryService> _library = new();
    private readonly Mock<IBenefitPlanService> _plans = new();
    private readonly Mock<IUserContextService> _user = new();

    public FeeScheduleLibraryPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_library.Object);
        Services.AddSingleton(_plans.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "admin-1", Roles = ["TenantAdmin"] });
        _user.Setup(u => u.HasPermission("benefits:read")).Returns(true);
        _user.Setup(u => u.HasPermission("settings:manage")).Returns(true);
        _plans.Setup(p => p.SearchBenefitPlansAsync(null, null)).ReturnsAsync([new BenefitPlanListItem { PlanId = "TX-STAR-2027" }]);

        _library.Setup(l => l.ListLibraryAsync(null)).ReturnsAsync(
        [
            new() { Id = MedicareId, Name = "Medicare PFS 2026 TX DALLAS (18)", Type = "MedicareMpfs", Jurisdiction = "TX", CodeSystem = "HCPCS", SourceVersion = "RVU26A", EffectiveDate = new DateTime(2026, 1, 1) },
            new() { Id = "lib:TMHP", Name = "TMHP adult acute care", Type = "Medicaid", Jurisdiction = "TX", CodeSystem = "Local", EffectiveDate = new DateTime(2026, 1, 1) },
        ]);
        _library.Setup(l => l.GetAsync(true, MedicareId, null, 0, 50)).ReturnsAsync(new FeeScheduleDetailView
        {
            Schedule = new() { Id = MedicareId, Name = "Medicare PFS 2026 TX DALLAS (18)", Type = "MedicareMpfs", CodeSystem = "HCPCS", ConversionFactor = 33.4009m },
            LineCount = 9499, MatchingCount = 9499,
            Lines = [new() { ProcedureCode = "99213", RateType = "Rvu", Amount = 87.01m, FacilityAmount = 65.08m }],
        });
    }

    private IRenderedComponent<FeeScheduleLibrary> OpenMedicare()
    {
        var cut = RenderComponent<FeeScheduleLibrary>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=library-row]").Should().HaveCount(2));
        cut.FindAll("[data-testid=library-row]").First(r => r.TextContent.Contains("Medicare")).Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=schedule-detail]"));
        return cut;
    }

    private static AngleSharp.Dom.IElement Input(IRenderedFragment cut, string testId) =>
        cut.Find($"input[data-testid={testId}], [data-testid={testId}] input");

    [Fact]
    public void Opening_a_library_schedule_shows_its_rates()
    {
        var cut = OpenMedicare();

        cut.Find("[data-testid=schedule-detail]").TextContent.Should().Contain("9,499 lines").And.Contain("33.4009");
        cut.Find("[data-testid=schedule-line]").TextContent.Should().Contain("99213").And.Contain("$87.01").And.Contain("$65.08");
    }

    [Fact]
    public void A_CPT_schedule_cannot_be_deployed_until_the_license_is_confirmed()
    {
        var cut = OpenMedicare();
        cut.Find("[data-testid=plan-chip]").Click();

        cut.Find("[data-testid=deploy-button]").HasAttribute("disabled").Should().BeTrue();
        Input(cut, "deploy-license").Change(true);

        cut.WaitForAssertion(() => cut.Find("[data-testid=deploy-button]").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Preview_shows_library_and_deployed_amounts_without_writing()
    {
        _library.Setup(l => l.DeployAsync(It.Is<FeeScheduleDeployInput>(i => i.DryRun))).ReturnsAsync(new FeeScheduleDeployView
        {
            DryRun = true, Percent = 110, Outcome = "WouldWrite", TargetName = "Medicare PFS 2026 TX DALLAS (18) at 110%", LineCount = 9499,
            Preview = [new() { ProcedureCode = "99213", LibraryAmount = 87.01m, DeployedAmount = 95.71m }],
        });
        var cut = OpenMedicare();
        Input(cut, "deploy-percent").Change("110");

        cut.Find("[data-testid=deploy-preview-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=deploy-preview-row]").TextContent.Should().Contain("$87.01").And.Contain("$95.71"));
        cut.Find("[data-testid=deploy-result]").TextContent.Should().Contain("would be written");
        _library.Verify(l => l.DeployAsync(It.Is<FeeScheduleDeployInput>(i => i.DryRun && i.Percent == 110 && i.SourceId == MedicareId)));
    }

    [Fact]
    public void Deploy_sends_the_plan_percent_and_license_confirmation()
    {
        _library.Setup(l => l.DeployAsync(It.Is<FeeScheduleDeployInput>(i => !i.DryRun))).ReturnsAsync(new FeeScheduleDeployView
        {
            Percent = 110, Outcome = "Created", TargetName = "Medicare PFS 2026 TX DALLAS (18) at 110%", LineCount = 9499,
        });
        var cut = OpenMedicare();
        Input(cut, "deploy-percent").Change("110");
        Input(cut, "deploy-plan").Input("TX-STAR-2027");
        Input(cut, "deploy-license").Change(true);

        cut.WaitForAssertion(() => cut.Find("[data-testid=deploy-button]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=deploy-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=deploy-result]").TextContent.Should().Contain("Deployed 9499 lines"));
        _library.Verify(l => l.DeployAsync(It.Is<FeeScheduleDeployInput>(i =>
            !i.DryRun && i.PlanId == "TX-STAR-2027" && i.Percent == 110 && i.LicenseAcknowledged && i.Name == null)));
    }

    [Fact]
    public void A_refused_deploy_shows_the_reason()
    {
        _library.Setup(l => l.DeployAsync(It.IsAny<FeeScheduleDeployInput>()))
            .ReturnsAsync(new FeeScheduleDeployView { Error = "Plan 'NOPE' not found in this tenant." });
        var cut = OpenMedicare();

        cut.Find("[data-testid=deploy-preview-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=deploy-error]").TextContent.Should().Contain("not found"));
    }

    [Fact]
    public void Without_settings_manage_the_library_is_read_only()
    {
        _user.Setup(u => u.HasPermission("settings:manage")).Returns(false);
        var cut = OpenMedicare();

        cut.Find("[data-testid=deploy-needs-permission]");
        cut.FindAll("[data-testid=deploy-button]").Should().BeEmpty();
    }

    [Fact]
    public void An_empty_library_says_so()
    {
        _library.Setup(l => l.ListLibraryAsync(null)).ReturnsAsync([]);
        var cut = RenderComponent<FeeScheduleLibrary>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=library-empty]"));
    }
}

public class FeeScheduleLibraryServiceClientTests
{
    private readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:BenefitPlanService"] = "http://bp/api" })
        .Build();

    private PortalFeeScheduleLibraryService Create(FakeHandler handler) =>
        new(new HttpClient(handler), _configuration, Mock.Of<ILogger<PortalFeeScheduleLibraryService>>());

    [Fact]
    public async Task Schedule_ids_travel_as_an_escaped_query_parameter()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"schedule":{"id":"x"},"lineCount":1,"matchingCount":1,"lines":[]}""");

        await Create(handler).GetAsync(true, "lib:Medicare PFS 2026 TX:20260101", "992", 50, 50);

        handler.CapturedRequests.Single().RequestUri!.AbsoluteUri.Should()
            .Be("http://bp/api/v1/fee-schedule-library/schedule?id=lib%3AMedicare%20PFS%202026%20TX%3A20260101&skip=50&take=50&code=992");
    }

    [Fact]
    public async Task Tenant_schedules_come_from_the_tenant_route()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        await Create(handler).ListTenantAsync();
        handler.CapturedUrls.Single().Should().Be("http://bp/api/v1/fee-schedules");
    }

    [Fact]
    public async Task A_bad_request_report_carries_the_services_error()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest, """{"error":"This schedule is keyed by CPT/HCPCS codes.","percent":110}""");

        var result = await Create(handler).DeployAsync(new FeeScheduleDeployInput { SourceId = "s", DryRun = false });

        result.Error.Should().Contain("CPT");
        var body = JsonNode.Parse(await handler.CapturedRequests.Single().Content!.ReadAsStringAsync())!;
        body["dryRun"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Forbidden_names_the_permission()
    {
        var result = await Create(new FakeHandler(HttpStatusCode.Forbidden)).DeployAsync(new FeeScheduleDeployInput { SourceId = "s" });
        result.Error.Should().Contain("settings:manage");
    }
}
