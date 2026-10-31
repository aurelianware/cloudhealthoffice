using BenefitPlanService.Models;
using BenefitPlanService.Templates;

namespace CloudHealthOffice.BenefitPlanService.Tests.Templates;

public class ArizonaNewYorkBenefitPlanTemplateTests
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

    private static BenefitPlan Plan(string id) =>
        Catalog.Get(id)!.ToBenefitPlan("P-1", null, "Example plan", new DateTime(2027, 1, 1), null, null);

    private static Benefit B(BenefitPlan p, string category) => p.Benefits.Single(b => b.ServiceCategory == category);

    [Fact]
    public void ArizonaAndNewYorkTemplatesAreInTheCatalog()
    {
        Assert.Equal(["az-ahcccs-adult", "az-ahcccs-child", "az-altcs-epd", "az-kidscare"], Catalog.List("AZ").Select(t => t.TemplateId));
        Assert.Equal(["ny-child-health-plus", "ny-harp", "ny-mltc", "ny-mmc-adult", "ny-mmc-child"], Catalog.List("NY").Select(t => t.TemplateId));
    }

    [Theory]
    [InlineData("az-ahcccs-child")]
    [InlineData("az-ahcccs-adult")]
    [InlineData("az-altcs-epd")]
    [InlineData("az-kidscare")]
    [InlineData("ny-mmc-child")]
    [InlineData("ny-mmc-adult")]
    [InlineData("ny-harp")]
    [InlineData("ny-child-health-plus")]
    public void TemplatesHaveNoClaimCostSharing(string id) =>
        Assert.All(Plan(id).Benefits.Where(b => b.IsCovered), b => Assert.True(b.InNetworkCopay is null or 0m, $"{id}/{b.ServiceCategory}"));

    [Fact]
    public void ArizonaAdultCopaysAreDocumentedNotCharged()
    {
        var t = Catalog.Get("az-ahcccs-adult")!;
        Assert.Contains(t.NotModeled, n => n.Source == "ahcccs-copayments" && n.Why!.Contains("5% of household income"));
        Assert.Contains("15 visits per contract year", B(Plan("az-ahcccs-adult"), "Physical Therapy").Limitations);
        Assert.Null(B(Plan("az-ahcccs-adult"), "Physical Therapy").VisitLimit);
    }

    [Theory]
    [InlineData("ny-mmc-child")]
    [InlineData("ny-mmc-adult")]
    [InlineData("ny-harp")]
    public void NewYorkManagedCarePharmacyIsCarvedOutToNyRx(string id)
    {
        var t = Catalog.Get(id)!;
        Assert.Contains(t.CarveOuts, c => c.To!.StartsWith("NYRx"));
        Assert.Contains("NYRx", B(Plan(id), "Pharmacy").Limitations);
        Assert.All(t.DisplayBenefits, d => Assert.False(d.Covered));
    }

    [Fact]
    public void NewYorkMltcCoversOnlyLongTermCareServices()
    {
        var covered = Plan("ny-mltc").Benefits.Where(b => b.IsCovered).Select(b => b.ServiceCategory).OrderBy(c => c);
        Assert.Equal(["Ambulance", "DME", "Home Health", "Physical Therapy", "Skilled Nursing", "Vision"], covered);
    }

    [Fact]
    public void ChildHealthPlusKeepsPharmacyInPlan()
    {
        var t = Catalog.Get("ny-child-health-plus")!;
        Assert.Empty(t.CarveOuts);
        Assert.True(B(Plan("ny-child-health-plus"), "Pharmacy").IsCovered);
    }
}
