using System.Text.Json.Nodes;
using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Guided plan onboarding: the template gallery (pick, review, create a
/// Draft) and the Draft page (edit, check each verify value, publish).
/// </summary>
public class PlanTemplatesPageTests : TestContext
{
    private readonly Mock<IPlanTemplateService> _templates = new();
    private readonly Mock<IUserContextService> _user = new();

    public PlanTemplatesPageTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_templates.Object);
        Services.AddSingleton(_user.Object);
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "admin-1", Roles = ["TenantAdmin"] });
        _user.Setup(u => u.HasPermission("benefits:read")).Returns(true);
        _user.Setup(u => u.HasPermission("settings:manage")).Returns(true);

        _templates.Setup(t => t.ListTemplatesAsync(null, null)).ReturnsAsync(
        [
            new() { TemplateId = "tx-chip-186", State = "TX", Program = "CHIP", PlanName = "Texas CHIP 186", Population = "Children", VerifyCount = 1 },
            new() { TemplateId = "ca-medi-cal-adult", State = "CA", Program = "Medi-Cal", PlanName = "Medi-Cal adult", Population = "Adults", VerifyCount = 4 },
        ]);
        _templates.Setup(t => t.ListDraftsAsync()).ReturnsAsync(
        [
            new() { PlanId = "TX-OLD", VersionId = "v9", VersionNumber = 1, PlanName = "Old draft", Payer = "Acme", VersionState = "Draft" },
        ]);
        _templates.Setup(t => t.GetTemplateAsync("tx-chip-186")).ReturnsAsync(Template());
    }

    private static PlanTemplateDetail Template() => new()
    {
        TemplateId = "tx-chip-186", State = "TX", Program = "CHIP", PlanName = "Texas CHIP 186", LineOfBusiness = "Medicaid",
        Benefits =
        [
            new() { ServiceCategory = "Office Visit", Copay = 20, Source = "hhsc", Status = "sourced" },
            new() { ServiceCategory = "Inpatient", Copay = 75, Status = "verify" },
        ],
        CarveOuts = [new() { Service = "Dental", To = "CHIP DMO" }],
        NotModeled = [new() { Item = "Formulary", Why = "Vendor Drug Program" }],
        Sources = new() { ["hhsc"] = new() { Title = "HHSC D-1800", Url = "https://example.test" } },
    };

    private IRenderedComponent<PlanTemplates> OpenGallery()
    {
        var cut = RenderComponent<PlanTemplates>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=template-row]").Should().HaveCount(2));
        return cut;
    }

    private static AngleSharp.Dom.IElement Input(IRenderedFragment cut, string testId) =>
        cut.Find($"input[data-testid={testId}], [data-testid={testId}] input");

    private static void Select(IRenderedComponent<PlanTemplates> cut, string name) =>
        cut.FindAll("[data-testid=template-row]").First(r => r.TextContent.Contains(name)).Click();

    [Fact]
    public void Lists_templates_and_the_tenants_drafts()
    {
        var cut = OpenGallery();

        cut.Markup.Should().Contain("Texas CHIP 186").And.Contain("Medi-Cal adult");
        cut.Markup.Should().Contain("Draft plans (1)");
    }

    [Fact]
    public void Choosing_a_template_shows_its_benefits_with_verify_flags_carve_outs_and_gaps()
    {
        var cut = OpenGallery();

        Select(cut, "Texas CHIP 186");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=template-benefit]").Should().HaveCount(2));
        var rows = cut.FindAll("[data-testid=template-benefit]").ToList();
        rows[0].TextContent.Should().Contain("Office Visit").And.Contain("$20").And.Contain("sourced");
        rows[1].TextContent.Should().Contain("Inpatient").And.Contain("verify");
        cut.Find("[data-testid=template-carveouts]").TextContent.Should().Contain("CHIP DMO");
        cut.Find("[data-testid=template-notmodeled]").TextContent.Should().Contain("Formulary");
    }

    [Fact]
    public void Without_settings_manage_the_template_can_be_read_but_not_deployed()
    {
        _user.Setup(u => u.HasPermission("settings:manage")).Returns(false);
        var cut = OpenGallery();

        Select(cut, "Texas CHIP 186");

        cut.WaitForAssertion(() => cut.Find("[data-testid=create-needs-permission]"));
        cut.FindAll("[data-testid=create-draft]").Should().BeEmpty();
    }

    [Fact]
    public void Creating_a_draft_requires_plan_id_and_payer()
    {
        var cut = OpenGallery();
        Select(cut, "Texas CHIP 186");
        cut.WaitForAssertion(() => cut.Find("[data-testid=create-draft]"));

        cut.Find("[data-testid=create-draft]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=create-error]").TextContent.Should().Contain("required"));
        _templates.Verify(t => t.CreateDraftFromTemplateAsync(It.IsAny<string>(), It.IsAny<CreateDraftFromTemplateRequest>()), Times.Never);
    }

    [Fact]
    public void Creating_a_draft_opens_it_with_the_template_for_review()
    {
        _templates.Setup(t => t.CreateDraftFromTemplateAsync("tx-chip-186", It.IsAny<CreateDraftFromTemplateRequest>()))
            .ReturnsAsync(PlanWriteResult.Ok("TX-CHIP-2027", "v-1"));
        var cut = OpenGallery();
        Select(cut, "Texas CHIP 186");
        cut.WaitForAssertion(() => cut.Find("[data-testid=create-draft]"));

        Input(cut, "draft-plan-id").Change(" TX-CHIP-2027 ");
        Input(cut, "draft-payer").Change("Acme Health");
        cut.Find("[data-testid=create-draft]").Click();

        cut.WaitForAssertion(() =>
            Services.GetRequiredService<FakeNavigationManager>().Uri.Should().EndWith("/benefit-plans/drafts/TX-CHIP-2027/v-1?template=tx-chip-186"));
        _templates.Verify(t => t.CreateDraftFromTemplateAsync("tx-chip-186",
            It.Is<CreateDraftFromTemplateRequest>(r => r.PlanId == "TX-CHIP-2027" && r.Payer == "Acme Health")));
    }

    [Fact]
    public void A_refused_draft_shows_the_services_reason()
    {
        _templates.Setup(t => t.CreateDraftFromTemplateAsync("tx-chip-186", It.IsAny<CreateDraftFromTemplateRequest>()))
            .ReturnsAsync(PlanWriteResult.Fail("Plan 'X' already exists; amend it instead."));
        var cut = OpenGallery();
        Select(cut, "Texas CHIP 186");
        cut.WaitForAssertion(() => cut.Find("[data-testid=create-draft]"));

        Input(cut, "draft-plan-id").Change("X");
        Input(cut, "draft-payer").Change("Acme");
        cut.Find("[data-testid=create-draft]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=create-error]").TextContent.Should().Contain("already exists"));
    }

    // ── Draft page ──

    private static JsonObject Draft(string state = "Draft") => new()
    {
        ["id"] = "row-1", ["planId"] = "TX-CHIP-2027", ["versionId"] = "v-1", ["versionNumber"] = 1,
        ["versionState"] = state, ["planName"] = "Texas CHIP 186", ["payer"] = "Acme",
        ["effectiveDate"] = "2027-01-01T00:00:00Z",
        ["costSharing"] = new JsonObject { ["individualDeductible"] = 0, ["monthlyPremium"] = 0 },
        ["accumulatorTargets"] = new JsonArray(new JsonObject { ["kept"] = true }),
        ["benefits"] = new JsonArray(
            new JsonObject { ["benefitType"] = "Medical", ["serviceCategory"] = "Office Visit", ["isCovered"] = true,
                ["inNetworkCopay"] = 20, ["outNetworkCopay"] = 20, ["inNetworkCoinsurance"] = 0, ["outNetworkCoinsurance"] = 0 },
            new JsonObject { ["benefitType"] = "Medical", ["serviceCategory"] = "Inpatient", ["isCovered"] = true,
                ["inNetworkCopay"] = 75, ["outNetworkCopay"] = 75, ["inNetworkCoinsurance"] = 0, ["outNetworkCoinsurance"] = 0 }),
    };

    private IRenderedComponent<PlanDraftEditor> OpenDraft(JsonObject draft, string? template = "tx-chip-186")
    {
        _templates.Setup(t => t.GetVersionAsync("TX-CHIP-2027", "v-1")).ReturnsAsync(draft);
        if (template != null)
            Services.GetRequiredService<FakeNavigationManager>().NavigateTo($"/benefit-plans/drafts/TX-CHIP-2027/v-1?template={template}");
        var cut = RenderComponent<PlanDraftEditor>(p => p.Add(x => x.PlanId, "TX-CHIP-2027").Add(x => x.VersionId, "v-1"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=draft-benefit]").Should().HaveCount(2));
        return cut;
    }

    [Fact]
    public void Publishing_waits_until_every_verify_value_is_checked()
    {
        var cut = OpenDraft(Draft());

        cut.Find("[data-testid=verify-progress]").TextContent.Should().Contain("0 of 1");
        cut.Find("[data-testid=publish-draft]").HasAttribute("disabled").Should().BeTrue();

        Input(cut, "verify-check").Change(true);

        cut.WaitForAssertion(() => cut.Find("[data-testid=publish-draft]").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Saving_changes_only_the_edited_values_and_keeps_the_rest_of_the_plan()
    {
        JsonObject? saved = null;
        _templates.Setup(t => t.SaveDraftAsync(It.IsAny<JsonObject>()))
            .Callback<JsonObject>(d => saved = (JsonObject)d.DeepClone())
            .ReturnsAsync(PlanWriteResult.Ok("TX-CHIP-2027", "v-1"));
        var cut = OpenDraft(Draft());

        cut.FindAll("[data-testid=draft-benefit]").ElementAt(1).QuerySelectorAll("input").ElementAt(1).Change("100");
        cut.Find("[data-testid=save-draft]").Click();

        cut.WaitForAssertion(() => saved.Should().NotBeNull());
        var inpatient = saved!["benefits"]![1]!;
        inpatient["inNetworkCopay"]!.GetValue<decimal>().Should().Be(100);
        inpatient["outNetworkCopay"]!.GetValue<decimal>().Should().Be(100, "out of network matched in network, so it follows");
        saved["benefits"]![0]!["inNetworkCopay"]!.GetValue<decimal>().Should().Be(20);
        saved["accumulatorTargets"].Should().NotBeNull();
        _templates.Verify(t => t.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime?>()), Times.Never);
    }

    [Fact]
    public void Save_and_publish_saves_first_then_publishes_and_returns_to_plans()
    {
        _templates.Setup(t => t.SaveDraftAsync(It.IsAny<JsonObject>())).ReturnsAsync(PlanWriteResult.Ok("TX-CHIP-2027", "v-1"));
        _templates.Setup(t => t.PublishAsync("TX-CHIP-2027", "v-1", null)).ReturnsAsync(PlanWriteResult.Ok("TX-CHIP-2027", "v-1"));
        var cut = OpenDraft(Draft(), template: null);

        cut.Find("[data-testid=publish-draft]").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<FakeNavigationManager>().Uri.Should().EndWith("/benefit-plans"));
        _templates.Verify(t => t.SaveDraftAsync(It.IsAny<JsonObject>()), Times.Once);
        _templates.Verify(t => t.PublishAsync("TX-CHIP-2027", "v-1", null), Times.Once);
    }

    [Fact]
    public void A_published_version_is_read_only()
    {
        var cut = OpenDraft(Draft("Published"), template: null);

        cut.Find("[data-testid=not-draft]").TextContent.Should().Contain("Published");
        cut.FindAll("[data-testid=save-draft]").Should().BeEmpty();
        cut.FindAll("[data-testid=publish-draft]").Should().BeEmpty();
    }
}
