using System.Text.Json;
using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Services;
using BenefitPlanService.Templates;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace CloudHealthOffice.BenefitPlanService.Tests.Templates;

public class TexasBenefitPlanTemplateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly BenefitPlanTemplateCatalog Catalog =
        BenefitPlanTemplateCatalog.LoadFrom(Path.Combine(RepoRoot(), BenefitPlanTemplateCatalog.DefaultDirectory));

    private static readonly string[] ExpectedTexasTemplates =
    [
        "tx-chip-151", "tx-chip-186", "tx-chip-201",
        "tx-chip-perinatal-above-threshold", "tx-chip-perinatal-at-or-below-threshold",
        "tx-star-adult", "tx-star-child", "tx-star-health", "tx-star-kids", "tx-starplus",
    ];

    public static IEnumerable<object[]> TemplateIds => ExpectedTexasTemplates.Select(id => new object[] { id });

    private static BenefitPlan Plan(string templateId) =>
        Catalog.Get(templateId)!.ToBenefitPlan("P-1", null, "Example MCO", new DateTime(2027, 1, 1), null, "prog-1");

    private static Benefit B(BenefitPlan plan, string category) => plan.Benefits.Single(b => b.ServiceCategory == category);

    [Fact]
    public void TheTexasCatalogHasEveryProgram()
    {
        Assert.Equal(ExpectedTexasTemplates, Catalog.List("TX").Select(t => t.TemplateId));
        Assert.Equal(3, Catalog.List("tx", "CHIP").Count);
    }

    [Theory]
    [MemberData(nameof(TemplateIds))]
    public void EveryTemplateIsCompleteAndCited(string templateId)
    {
        var t = Catalog.Get(templateId)!;

        // Every adjudication category a claim line can resolve to is present once.
        var codes = SystemDefaultServiceTypeCodes();
        Assert.Equal(codes.OrderBy(c => c), t.Benefits.Select(b => b.ServiceCategory).OrderBy(c => c));

        // Every value has a source that the template lists, and a known status.
        foreach (var b in t.Benefits.Concat(t.DisplayBenefits))
        {
            Assert.False(string.IsNullOrWhiteSpace(b.Source), $"{templateId}/{b.ServiceCategory} has no source");
            Assert.True(t.Sources.ContainsKey(b.Source!), $"{templateId}/{b.ServiceCategory} cites unknown source {b.Source}");
            Assert.Contains(b.Status, new[] { TemplateStatus.Sourced, TemplateStatus.Verify });
        }
        foreach (var n in t.NotModeled.Concat(t.CarveOuts).Where(n => n.Source is not null))
            Assert.True(t.Sources.ContainsKey(n.Source!), $"{templateId} note cites unknown source {n.Source}");

        // Medicaid and CHIP never carry a deductible or coinsurance.
        Assert.All(t.Benefits, b => Assert.False(b.DeductibleApplies));
        Assert.All(t.Benefits, b => Assert.Equal(0m, b.Coinsurance ?? 0m));
        Assert.Equal(LineOfBusiness.Medicaid, t.LineOfBusiness);
    }

    [Theory]
    [InlineData("tx-star-child")]
    [InlineData("tx-star-adult")]
    [InlineData("tx-starplus")]
    [InlineData("tx-star-kids")]
    [InlineData("tx-star-health")]
    public void MedicaidProgramsHaveNoMemberCostSharing(string templateId)
    {
        var plan = Plan(templateId);
        Assert.All(plan.Benefits, b =>
        {
            Assert.True(b.IsCovered);
            Assert.Null(b.InNetworkCopay);
            Assert.Equal(0m, b.InNetworkCoinsurance);
        });
        Assert.Equal(0m, plan.CostSharing.IndividualDeductible);
        Assert.Equal(0m, plan.CostSharing.IndividualOutOfPocketMax);
        Assert.Equal(PlanType.Medicaid, plan.PlanType);
        Assert.Equal("prog-1", plan.ProgramId);
    }

    [Theory]
    [InlineData("tx-chip-151", 5, 35, null, 5)]
    [InlineData("tx-chip-186", 20, 75, 10, 35)]
    [InlineData("tx-chip-201", 25, 125, 10, 35)]
    public void ChipCopaysFollowTheHhscTable(string templateId, int doctor, int inpatient, int? generic, int brand)
    {
        var plan = Plan(templateId);
        Assert.Equal(doctor, B(plan, "Office Visit").InNetworkCopay);
        Assert.Equal(doctor, B(plan, "Specialist Visit").InNetworkCopay);
        Assert.Equal(inpatient, B(plan, "Inpatient Hospital").InNetworkCopay);
        Assert.Equal(generic.HasValue ? generic.Value : null, B(plan, "Pharmacy").InNetworkCopay);
        Assert.Equal(brand, B(plan, "Preferred Brand").InNetworkCopay);
        Assert.Null(B(plan, "Preventive Care").InNetworkCopay);
        Assert.Null(B(plan, "Emergency Room").InNetworkCopay);
        Assert.Null(B(plan, "Outpatient Hospital").InNetworkCopay);
        Assert.Equal(20000m, B(plan, "DME").AnnualMaximum);

        // A copay with no deductible applies instead of the deductible.
        Assert.False(B(plan, "Office Visit").DeductibleApplies);
        Assert.True(Assert.IsType<PreventiveBenefit>(B(plan, "Preventive Care")).IsAcaPreventive);

        var t = Catalog.Get(templateId)!;
        Assert.Contains(t.NotModeled, n => n.Item!.StartsWith("Cost-sharing cap of 5%"));
        Assert.Contains(t.NotModeled, n => n.Item!.StartsWith("Non-emergency ER copay"));
        Assert.Contains(t.CarveOuts, c => c.Service == "Dental");
    }

    [Theory]
    [InlineData("tx-chip-perinatal-above-threshold", true)]
    [InlineData("tx-chip-perinatal-at-or-below-threshold", false)]
    public void ChipPerinatalCoversThePregnancyOnly(string templateId, bool facilityCovered)
    {
        var plan = Plan(templateId);
        Assert.True(B(plan, "Maternity").IsCovered);
        Assert.True(B(plan, "Office Visit").IsCovered);
        Assert.Equal(facilityCovered, B(plan, "Inpatient Hospital").IsCovered);
        foreach (var excluded in new[] { "Behavioral Health", "DME", "Home Health", "Hospice", "Skilled Nursing", "Physical Therapy", "Vision" })
            Assert.False(B(plan, excluded).IsCovered, $"{excluded} should be excluded for a perinate");
        Assert.All(plan.Benefits, b => Assert.Null(b.InNetworkCopay));
    }

    [Fact]
    public void AdultProgramsCarryTheSpellOfIllnessLimitAsText()
    {
        var plan = Plan("tx-starplus");
        Assert.Contains("30-day", B(plan, "Inpatient Hospital").Limitations);
        Assert.Null(B(plan, "Inpatient Hospital").VisitLimit);
        Assert.Contains(Catalog.Get("tx-starplus")!.NotModeled, n => n.Item!.StartsWith("Long-term services"));
    }

    [Fact]
    public void DuplicateTemplateIdsAreRejected()
    {
        var t = new BenefitPlanTemplate { TemplateId = "x" };
        Assert.Throws<InvalidOperationException>(() => new BenefitPlanTemplateCatalog([t, new BenefitPlanTemplate { TemplateId = "X" }]));
    }

    [Fact]
    public async Task CreateDraftWritesADraftAndRefusesAnExistingPlanId()
    {
        var plans = Substitute.For<IBenefitPlanService>();
        plans.ListVersionsAsync("NEW", "tenant-1", 1, null).Returns((Array.Empty<BenefitPlan>(), (string?)null));
        plans.ListVersionsAsync("OLD", "tenant-1", 1, null).Returns((new[] { new BenefitPlan { VersionId = "V0" } }, (string?)null));
        plans.CreateDraftAsync(Arg.Any<BenefitPlan>(), "tenant-1", "user-1").Returns(ci => { var p = ci.Arg<BenefitPlan>(); p.VersionId = "V1"; return p; });
        var actor = Substitute.For<ICurrentActor>();
        actor.UserId.Returns("user-1");

        var controller = new BenefitPlanTemplatesController(Catalog, plans, actor)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Items["TenantId"] = "tenant-1";

        var request = new CreateFromTemplateRequest { PlanId = "NEW", Payer = "Example MCO", EffectiveDate = new DateTime(2027, 1, 1) };
        var created = Assert.IsType<CreatedResult>((await controller.CreateDraft("tx-chip-186", request)).Result);
        var draft = Assert.IsType<BenefitPlan>(created.Value);
        Assert.Equal("Texas CHIP (Above 151% up to 186% FPL)", draft.PlanName);
        await plans.Received(1).CreateDraftAsync(Arg.Is<BenefitPlan>(p => p.PlanId == "NEW"), "tenant-1", "user-1");

        request.PlanId = "OLD";
        Assert.IsType<ConflictObjectResult>((await controller.CreateDraft("tx-chip-186", request)).Result);
        Assert.IsType<NotFoundObjectResult>((await controller.CreateDraft("tx-nope", request)).Result);
    }

    private static HashSet<string> SystemDefaultServiceTypeCodes()
    {
        var path = Path.Combine(RepoRoot(), "schemas/service-category-mappings/system-defaults.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("mappings").EnumerateArray()
            .Select(m => m.GetProperty("serviceTypeCode").GetString()!)
            .ToHashSet();
    }
}
