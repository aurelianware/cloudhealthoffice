using System.Text.Json;
using BenefitPlanService.Models;
using BenefitPlanService.Templates;

namespace CloudHealthOffice.BenefitPlanService.Tests.Templates;

public class CaliforniaFloridaBenefitPlanTemplateTests
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

    public static IEnumerable<object[]> AllTemplateIds => Catalog.List().Select(t => new object[] { t.TemplateId });

    private static BenefitPlan Plan(string id) =>
        Catalog.Get(id)!.ToBenefitPlan("P-1", null, "Example plan", new DateTime(2027, 1, 1), null, null);

    private static Benefit B(BenefitPlan p, string category) => p.Benefits.Single(b => b.ServiceCategory == category);

    [Fact]
    public void CaliforniaAndFloridaTemplatesAreInTheCatalog()
    {
        Assert.Equal(["ca-medi-cal-adult", "ca-medi-cal-child"], Catalog.List("CA").Select(t => t.TemplateId));
        Assert.Equal(["fl-healthy-kids", "fl-ltc", "fl-mma-adult", "fl-mma-child"], Catalog.List("FL").Select(t => t.TemplateId));
    }

    [Theory]
    [MemberData(nameof(AllTemplateIds))]
    public void EveryTemplateInEveryStateIsCompleteAndCited(string id)
    {
        var t = Catalog.Get(id)!;
        var path = Path.Combine(RepoRoot(), "schemas/service-category-mappings/system-defaults.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var codes = doc.RootElement.GetProperty("mappings").EnumerateArray().Select(m => m.GetProperty("serviceTypeCode").GetString()!).OrderBy(c => c);

        Assert.Equal(codes, t.Benefits.Select(b => b.ServiceCategory).OrderBy(c => c));
        foreach (var b in t.Benefits.Concat(t.DisplayBenefits))
        {
            Assert.True(b.Source is not null && t.Sources.ContainsKey(b.Source), $"{id}/{b.ServiceCategory} cites unknown source {b.Source}");
            Assert.Contains(b.Status, new[] { TemplateStatus.Sourced, TemplateStatus.Verify });
            Assert.False(b.DeductibleApplies);
        }
        foreach (var n in t.NotModeled.Concat(t.CarveOuts).Where(n => n.Source is not null))
            Assert.True(t.Sources.ContainsKey(n.Source!), $"{id} note cites unknown source {n.Source}");
    }

    [Theory]
    [InlineData("ca-medi-cal-child")]
    [InlineData("ca-medi-cal-adult")]
    [InlineData("fl-mma-child")]
    [InlineData("fl-mma-adult")]
    public void MedicaidManagedCareTemplatesHaveNoMemberCostSharing(string id) =>
        Assert.All(Plan(id).Benefits, b => Assert.Null(b.InNetworkCopay));

    [Fact]
    public void MediCalPharmacyIsCarvedOutToMediCalRx()
    {
        var t = Catalog.Get("ca-medi-cal-adult")!;
        Assert.Contains(t.CarveOuts, c => c.To!.StartsWith("Medi-Cal Rx"));
        Assert.Contains(t.CarveOuts, c => c.Service == "Specialty mental health services");
        var plan = Plan("ca-medi-cal-adult");
        Assert.Contains("Medi-Cal Rx", B(plan, "Pharmacy").Limitations);
        Assert.All(plan.Benefits.Where(b => b.ServiceCategory is "Generic" or "Preferred Brand"), b => Assert.False(b.IsCovered));
    }

    [Fact]
    public void FloridaHealthyKidsCopaysFollowThePublishedSchedule()
    {
        var plan = Plan("fl-healthy-kids");
        Assert.Equal(5m, B(plan, "Office Visit").InNetworkCopay);
        Assert.Equal(5m, B(plan, "Specialist Visit").InNetworkCopay);
        Assert.Equal(10m, B(plan, "Emergency Room").InNetworkCopay);
        Assert.Equal(10m, B(plan, "Ambulance").InNetworkCopay);
        Assert.Null(B(plan, "Inpatient Hospital").InNetworkCopay);
        Assert.True(B(plan, "Inpatient Hospital").PriorAuthRequired);
        Assert.Equal(5m, B(plan, "Pharmacy").InNetworkCopay);
        Assert.Equal(5m, B(plan, "Physical Therapy").InNetworkCopay);
        Assert.Null(B(plan, "Preventive Care").InNetworkCopay);
        Assert.Null(B(plan, "Behavioral Health").InNetworkCopay);
        Assert.Equal("verify", Catalog.Get("fl-healthy-kids")!.Benefits.Single(b => b.ServiceCategory == "Urgent Care").Status);
    }

    [Fact]
    public void FloridaLongTermCareCoversOnlyLtcServices()
    {
        var plan = Plan("fl-ltc");
        var covered = plan.Benefits.Where(b => b.IsCovered).Select(b => b.ServiceCategory).OrderBy(c => c);
        Assert.Equal(["DME", "Home Health", "Hospice", "Skilled Nursing"], covered);
    }
}
