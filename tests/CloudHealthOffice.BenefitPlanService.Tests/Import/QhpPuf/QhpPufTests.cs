using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BenefitPlanService.Import.QhpPuf;
using BenefitPlanService.Models;
using BenefitPlanService.Models.Benefits;
using BenefitPlanService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CloudHealthOffice.BenefitPlanService.Tests.Import.QhpPuf;

internal static class QhpSamples
{
    public const string Dir = "docs/integration/samples/qhp-puf";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static Stream Open(string file) => File.OpenRead(Path.Combine(RepoRoot(), Dir, file));

    public static List<QhpRow> Rows(string file, IReadOnlyList<string> required) =>
        QhpPufFileReader.Read(Open(file), required, file).ToList();

    public static (BenefitPlan Plan, QhpPlanImportResult Result) MapSample(string planId)
    {
        var attrs = Rows("plan-attributes.csv", QhpPufFileReader.PlanAttributesRequired).Single(r => r["PlanId"] == planId);
        var benefits = Rows("benefits-cost-sharing.csv", QhpPufFileReader.BenefitsRequired).Where(r => r["PlanId"] == planId).ToList();
        return QhpPlanMapper.Map(attrs, benefits);
    }
}

public class QhpCostShareParserTests
{
    [Theory]
    [InlineData("$30", QhpCostShareKind.Amount, 30, false, null)]
    [InlineData("$1,250 Copay after deductible", QhpCostShareKind.Amount, 1250, true, null)]
    [InlineData("$25 Copay with deductible", QhpCostShareKind.Amount, 25, true, null)]
    [InlineData("$150 Copay per Day after deductible", QhpCostShareKind.Amount, 150, true, "day")]
    [InlineData("$500 Copay per Stay", QhpCostShareKind.Amount, 500, false, "stay")]
    [InlineData("No Charge", QhpCostShareKind.NoCharge, 0, false, null)]
    [InlineData("No Charge after deductible", QhpCostShareKind.NoCharge, 0, true, null)]
    [InlineData("Not Applicable", QhpCostShareKind.NotApplicable, null, false, null)]
    [InlineData("", QhpCostShareKind.Blank, null, false, null)]
    [InlineData("Copay varies", QhpCostShareKind.Unparsed, null, false, null)]
    public void ParsesCopayCells(string raw, QhpCostShareKind kind, int? value, bool afterDeductible, string? per)
    {
        var c = QhpCostShareParser.ParseCopay(raw);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(value.HasValue ? value.Value : null, c.Value);
        Assert.Equal(afterDeductible, c.AfterDeductible);
        Assert.Equal(per, c.Per);
    }

    [Theory]
    [InlineData("30%", 0.30, false)]
    [InlineData("20% Coinsurance after deductible", 0.20, true)]
    [InlineData("0% Coinsurance before deductible", 0.0, false)]
    public void ParsesCoinsuranceAsFraction(string raw, double fraction, bool afterDeductible)
    {
        var c = QhpCostShareParser.ParseCoinsurance(raw);
        Assert.Equal(QhpCostShareKind.Amount, c.Kind);
        Assert.Equal((decimal)fraction, c.Value);
        Assert.Equal(afterDeductible, c.AfterDeductible);
    }

    [Theory]
    [InlineData("$5,000", 5000)]
    [InlineData("$0", 0)]
    [InlineData("$7,000 per person | $14,000 per group", 7000)]
    [InlineData("Not Applicable", null)]
    [InlineData("", null)]
    public void ParsesDollarCells(string raw, int? expected) =>
        Assert.Equal(expected.HasValue ? expected.Value : null, QhpCostShareParser.ParseDollars(raw));
}

public class QhpPlanMapperTests
{
    [Fact]
    public void MapsPlanIdentityAndIntegratedCostSharing()
    {
        var (plan, result) = QhpSamples.MapSample("99901ZZ0010001-01");

        Assert.Equal("99901ZZ0010001-01", plan.PlanId);
        Assert.Equal("Example Silver HMO", plan.PlanName);
        Assert.Equal("Example Health Plan", plan.Payer);
        Assert.Equal(PlanType.HMO, plan.PlanType);
        Assert.Equal(MetalLevel.Silver, plan.MetalLevel);
        Assert.Equal(LineOfBusiness.Exchange, plan.LineOfBusiness);
        Assert.Equal(new DateTime(2027, 1, 1), plan.EffectiveDate);
        Assert.Equal(new DateTime(2027, 12, 31), plan.TerminationDate);

        Assert.Equal(5000m, plan.CostSharing.IndividualDeductible);
        Assert.Equal(5000m, plan.CostSharing.InNetworkDeductible);
        Assert.Equal(10000m, plan.CostSharing.FamilyDeductible);
        Assert.Equal(9800m, plan.CostSharing.IndividualOutOfPocketMax);
        Assert.Equal(19600m, plan.CostSharing.FamilyOutOfPocketMax);
        Assert.Equal(0.30m, plan.CostSharing.Coinsurance);
        Assert.Null(plan.CostSharing.OutNetworkIndividualDeductible);

        Assert.Equal("ZZN001", result.References["NetworkId"]);
        Assert.Equal("https://example.invalid/sbc/99901ZZ0010001-01.pdf", result.References["URLForSummaryofBenefitsCoverage"]);
    }

    [Fact]
    public void MapsBenefitRowsOntoAdjudicationCategories()
    {
        var (plan, result) = QhpSamples.MapSample("99901ZZ0010001-01");
        Benefit B(string category) => plan.Benefits.Single(b => b.ServiceCategory == category);

        // Copay with no deductible: "$30" + "No Charge" coinsurance.
        var office = B("Office Visit");
        Assert.Equal(30m, office.InNetworkCopay);
        Assert.Equal(0m, office.InNetworkCoinsurance);
        Assert.False(office.DeductibleApplies);
        Assert.Equal("Primary Care Visit to Treat an Injury or Illness", office.Description);

        // Coinsurance after deductible.
        var inpatient = B("Inpatient Hospital");
        Assert.Null(inpatient.InNetworkCopay);
        Assert.Equal(0.30m, inpatient.InNetworkCoinsurance);
        Assert.True(inpatient.DeductibleApplies);

        // HMO, nothing stated out of network: member carries 100%.
        Assert.Equal(1.0m, office.OutNetworkCoinsurance);
        // ER is stated out of network, so it keeps its own rule.
        Assert.Equal(0.30m, B("Emergency Room").OutNetworkCoinsurance);

        // ACA preventive.
        var preventive = Assert.IsType<PreventiveBenefit>(B("Preventive Care"));
        Assert.True(preventive.IsAcaPreventive);

        // Facility fee feeds both outpatient categories; the physician row is folded.
        Assert.Equal("Outpatient Facility Fee (e.g., Ambulatory Surgery Center)", result.CategoriesFilled["Outpatient Surgery"]);
        Assert.Equal("Outpatient Facility Fee (e.g., Ambulatory Surgery Center)", result.CategoriesFilled["Outpatient Hospital"]);
        Assert.Contains(result.FoldedBenefits, f => f.StartsWith("Outpatient Surgery Physician/Surgical Services"));
        Assert.Contains(result.FoldedBenefits, f => f.StartsWith("Imaging (CT/PET Scans, MRIs)"));

        // Visit limit.
        Assert.Equal(30, B("Physical Therapy").VisitLimit);
        Assert.Equal("Year", B("Physical Therapy").VisitLimitPeriod);

        // Pharmacy: adjudication row plus display tiers.
        var rx = Assert.IsType<PharmacyBenefit>(B("Pharmacy"));
        Assert.Equal(15m, rx.InNetworkCopay);
        var specialty = Assert.IsType<PharmacyBenefit>(B("Specialty Drug"));
        Assert.True(specialty.IsSpecialtyDrug);
        Assert.Equal(0.40m, specialty.InNetworkCoinsurance);

        // Per-day copay is flagged.
        Assert.Contains(result.Warnings, w => w.StartsWith("Skilled Nursing:") && w.Contains("per day"));

        // Not covered and unmapped rows are reported, not dropped silently.
        Assert.Contains(result.UnmappedBenefits, u => u.BenefitName == "Bariatric Surgery" && !u.IsCovered);
        Assert.Contains(result.UnmappedBenefits, u => u.BenefitName == "Chiropractic Care" && u.IsCovered);
        Assert.Empty(result.CategoriesMissing);
    }

    [Fact]
    public void SeparateDrugDeductibleAndHsaPlanAreMappedWithWarnings()
    {
        var (plan, result) = QhpSamples.MapSample("99902ZZ0020001-00");

        Assert.Equal(PlanType.HDHP, plan.PlanType);
        Assert.Equal(MetalLevel.Gold, plan.MetalLevel);
        Assert.Equal(new DateTime(2027, 1, 1), plan.EffectiveDate);
        Assert.Equal(2000m, plan.CostSharing.IndividualDeductible);
        Assert.Equal(6000m, plan.CostSharing.OutNetworkIndividualDeductible);
        Assert.Equal(14000m, plan.CostSharing.OutNetworkIndividualOutOfPocketMax);
        Assert.Contains(result.Warnings, w => w.Contains("separate $250 drug deductible"));
        Assert.Contains(result.Warnings, w => w.Contains("HDHP"));

        // "No Charge after deductible": the member pays until the deductible is met, then nothing.
        var office = plan.Benefits.Single(b => b.ServiceCategory == "Office Visit");
        Assert.True(office.DeductibleApplies);
        Assert.Equal(0m, office.InNetworkCoinsurance);
        Assert.Equal(0.50m, office.OutNetworkCoinsurance);

        Assert.Contains("Skilled Nursing", result.CategoriesMissing);
        Assert.Contains(result.UnmappedBenefits, u => u.BenefitName == "Acupuncture" && !u.IsCovered);
    }

    [Fact]
    public void EveryAdjudicationCategoryExistsInTheSystemDefaultMappings()
    {
        // Benefit.ServiceCategory must equal a serviceTypeCode or claims never find the benefit.
        var path = Path.Combine(QhpSamples.RepoRoot(), "schemas/service-category-mappings/system-defaults.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var codes = doc.RootElement.GetProperty("mappings").EnumerateArray()
            .Select(m => m.GetProperty("serviceTypeCode").GetString())
            .ToHashSet();

        foreach (var target in QhpBenefitCategoryMap.Targets.Where(t => t.Kind != QhpBenefitCategoryMap.QhpBenefitKind.PharmacyTier))
            Assert.True(codes.Contains(target.ChoCategory), $"'{target.ChoCategory}' is not a serviceTypeCode in system-defaults.json");
    }

    [Fact]
    public void PharmacyTierLabelsResolveToPharmacyInTheMemberView()
    {
        foreach (var target in QhpBenefitCategoryMap.Targets.Where(t => t.Kind == QhpBenefitCategoryMap.QhpBenefitKind.PharmacyTier))
            Assert.Equal(BenefitCategoryMap.Pharmacy, BenefitCategoryMap.Resolve(target.ChoCategory).Category);
    }
}

public class QhpPufImportServiceTests
{
    private readonly IBenefitPlanService _plans = Substitute.For<IBenefitPlanService>();
    private readonly QhpPufImportService _sut;

    public QhpPufImportServiceTests()
    {
        _plans.ListVersionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns((Array.Empty<BenefitPlan>(), (string?)null));
        _plans.CreateDraftAsync(Arg.Any<BenefitPlan>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => { var p = ci.Arg<BenefitPlan>(); p.VersionId = "V-" + p.PlanId; return p; });
        _sut = new QhpPufImportService(_plans, NullLogger<QhpPufImportService>.Instance);
    }

    private Task<QhpPufImportReport> Run(QhpPufImportOptions options) =>
        _sut.ImportAsync(QhpSamples.Open("plan-attributes.csv"), QhpSamples.Open("benefits-cost-sharing.csv"), options, "tenant-1", "user-1");

    [Fact]
    public async Task DryRunMapsWithoutWriting()
    {
        var report = await Run(new QhpPufImportOptions { StateCode = "ZZ" });

        Assert.True(report.DryRun);
        Assert.Equal(4, report.PlansMatched);
        Assert.Equal(3, report.Outcomes["WouldCreate"]);
        Assert.Equal(1, report.Outcomes["Skipped"]);
        Assert.All(report.Plans.Where(p => p.Outcome == QhpPlanImportOutcome.WouldCreate), p => Assert.NotNull(p.Plan));
        Assert.Equal(2, report.UnmappedBenefitCounts["Chiropractic Care"]);
        await _plans.DidNotReceiveWithAnyArgs().CreateDraftAsync(default!, default!, default!);
    }

    [Fact]
    public async Task RealRunCreatesDraftsAndLeavesExistingPlansAlone()
    {
        _plans.ListVersionsAsync("99901ZZ0010001-06", "tenant-1", 1, null)
            .Returns((new[] { new BenefitPlan { PlanId = "99901ZZ0010001-06", VersionId = "EXISTING" } }, (string?)null));

        var report = await Run(new QhpPufImportOptions { IssuerId = "99901", DryRun = false });

        var created = Assert.Single(report.Plans, p => p.Outcome == QhpPlanImportOutcome.Created);
        Assert.Equal("99901ZZ0010001-01", created.PlanId);
        Assert.Equal("V-99901ZZ0010001-01", created.VersionId);
        Assert.Null(created.Plan);
        var exists = Assert.Single(report.Plans, p => p.Outcome == QhpPlanImportOutcome.Exists);
        Assert.Equal("EXISTING", exists.VersionId);
        await _plans.Received(1).CreateDraftAsync(Arg.Is<BenefitPlan>(p => p.PlanId == "99901ZZ0010001-01"), "tenant-1", "user-1");
    }

    [Fact]
    public async Task FiltersByCsrVariantAndCapsPlans()
    {
        var variant = await Run(new QhpPufImportOptions { StateCode = "ZZ", CsrVariants = new HashSet<string> { "06" } });
        Assert.Equal("99901ZZ0010001-06", Assert.Single(variant.Plans).PlanId);
        Assert.Equal(10m, Assert.Single(variant.Plans).Plan!.Benefits.Single(b => b.ServiceCategory == "Office Visit").InNetworkCopay);

        var capped = await Run(new QhpPufImportOptions { StateCode = "ZZ", MaxPlans = 2 });
        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.PlansProcessed);
        Assert.Equal(4, capped.PlansMatched);
    }

    [Fact]
    public async Task PlanIdFilterAcceptsStandardComponentIds()
    {
        var report = await Run(new QhpPufImportOptions { PlanIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99901ZZ0010001" } });
        Assert.Equal(2, report.PlansProcessed);
    }

    [Fact]
    public async Task RequiresASelector() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Run(new QhpPufImportOptions()));

    [Fact]
    public async Task FilesInTheWrongFieldsAreRejected()
    {
        var ex = await Assert.ThrowsAsync<QhpPufFormatException>(() => _sut.ImportAsync(
            QhpSamples.Open("benefits-cost-sharing.csv"), QhpSamples.Open("plan-attributes.csv"),
            new QhpPufImportOptions { StateCode = "ZZ" }, "tenant-1", "user-1"));
        Assert.Contains("Plan Attributes file is missing column(s)", ex.Message);
    }

    [Fact]
    public async Task ReadsTheZipCmsPublishes()
    {
        static Stream Zip(string file)
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry(file);
                using var w = entry.Open();
                using var src = QhpSamples.Open(file);
                src.CopyTo(w);
            }
            ms.Position = 0;
            return ms;
        }

        var report = await _sut.ImportAsync(
            QhpPufFileReader.OpenCsv(Zip("plan-attributes.csv"), "plan-attributes-puf.zip"),
            // No .zip name: detected by its signature.
            QhpPufFileReader.OpenCsv(Zip("benefits-cost-sharing.csv"), "upload"),
            new QhpPufImportOptions { StateCode = "ZZ" }, "tenant-1", "user-1");
        Assert.Equal(3, report.Outcomes["WouldCreate"]);
    }

    [Fact]
    public void BlankCoverageCellFollowsStatedCostSharing()
    {
        var attrs = QhpRow.Of(("BusinessYear", "2027"), ("StateCode", "ZZ"), ("IssuerId", "1"), ("StandardComponentId", "S"),
            ("PlanId", "S-01"), ("PlanMarketingName", "P"), ("PlanType", "Indemnity"), ("MarketCoverage", "SHOP (Small Group)"));
        var rows = new[]
        {
            QhpRow.Of(("PlanId", "S-01"), ("BenefitName", "Specialist  visit"), ("CopayInnTier1", "$40"), ("CoinsInnTier1", ""), ("IsCovered", "")),
        };
        var (plan, result) = QhpPlanMapper.Map(attrs, rows);

        Assert.Equal(PlanType.PPO, plan.PlanType);
        Assert.Equal(LineOfBusiness.Commercial, plan.LineOfBusiness);
        Assert.Contains(result.Warnings, w => w.Contains("Indemnity"));
        var specialist = Assert.Single(plan.Benefits);
        Assert.True(specialist.IsCovered);
        Assert.Equal(40m, specialist.InNetworkCopay);
        Assert.Equal(new DateTime(2027, 1, 1), plan.EffectiveDate);
    }
}
