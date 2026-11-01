using System.IO.Compression;
using BenefitPlanService.Import.PriorAuthRules;
using BenefitPlanService.Import.PriorAuthRules.MediCal;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// Medi-Cal's TAR and Non-Benefit List (Provider Manual Part 2 PDFs) read
/// into rows and loaded into the tenant's CA PA rule set beside the plan's own
/// list. Lines follow the published layout: page header, Code | Description |
/// Benefit Restrictions table, wrapped descriptions, change marks, headings
/// between tables.
/// </summary>
public sealed class MediCalTarListTests
{
    private const string Tenant = "tenant-a";

    private static readonly string[] Cd6 =
    [
        "Part 2 – TAR and Non-Benefit List: Codes 60000 thru 69999",
        "‹‹Some CPT Category I codes are non-benefits for the general Medi-Cal population.››",
        "Surgery",
        "Endocrine System",
        "Thyroid Gland",
        "Incision",
        "Code Description Benefit Restrictions",
        "60000 Incision and drainage of thyroglossal duct cyst, infected Assistant Surgeon services not payable",
        "Laparoscopy",
        "Code Description Benefit Restrictions",
        "60650 Laparoscopy, surgical, with adrenalectomy Requires TAR, Primary Surgeon/Provider",
        "60660 Ablation of 1 or more thyroid nodule(s), one lobe or the isthmus, Requires TAR, Primary",
        "percutaneous, including imaging guidance, radiofrequency Surgeon/Provider",
        "60661 Ablation of 1 or more thyroid nodule(s), additional lobe Requires TAR, Primary Surgeon/Provider;",
        "Assistant Surgeon services not payable",
        "Nervous System",
        "Code Description Benefit Restrictions",
        "‹‹64999 Unlisted procedure, nervous system Non-Benefit››",
        "0721T Quantitative computed tomography tissue characterization Non-Benefit",
        "Page updated: August 2025",
        "12",
    ];

    [Fact]
    public void Rows_are_read_with_wrapped_text_marks_and_headings()
    {
        var rows = MediCalTarList.Parse(Cd6, "cd6");

        rows.Select(r => r.Code).Should().Equal("60000", "60650", "60660", "60661", "64999", "0721T");
        rows.Should().OnlyContain(r => r.List == "Codes 60000 thru 69999");
        rows[0].Restriction.Should().Be(MediCalTarRestriction.AssistantSurgeonNotPayable);
        rows[1].Restriction.Should().Be(MediCalTarRestriction.RequiresTar);
        rows[1].Description.Should().Be("Laparoscopy, surgical, with adrenalectomy");
        rows[2].Restriction.Should().Be(MediCalTarRestriction.RequiresTar);
        rows[3].Restriction.Should().Be(MediCalTarRestriction.RequiresTar | MediCalTarRestriction.AssistantSurgeonNotPayable);
        rows[4].Restriction.Should().Be(MediCalTarRestriction.NonBenefit);
        rows[5].Restriction.Should().Be(MediCalTarRestriction.NonBenefit);
    }

    [Fact]
    public async Task Pdfs_load_tar_and_non_benefit_codes_beside_the_plans_own_list()
    {
        var repo = new PriorAuthRuleImportServiceTests.FakeRepo();
        var importer = new PriorAuthRuleImportService(repo, NullLogger<PriorAuthRuleImportService>.Instance);
        var svc = new MediCalTarImportService(importer);

        await importer.ImportAsync(new MemoryStream("Category,Code\nKnee,27447\n"u8.ToArray()),
            new PriorAuthRuleImportOptions { StateCode = "CA", LineOfBusiness = PaLineOfBusiness.Medicaid, DryRun = false },
            Tenant, "admin");

        var report = await svc.ImportAsync([("tarandnoncd6.pdf", Pdf(Cd6))],
            new MediCalTarImportOptions { DryRun = false, IncludeCodes = true }, Tenant, "admin");

        report.RowsRead.Should().Be(6);
        report.RequiresTar.Should().Be(3);
        report.NonBenefit.Should().Be(2);
        report.NoTar.Should().Be(1);
        report.Files.Should().ContainSingle().Which.List.Should().Be("Codes 60000 thru 69999");
        report.Import.Outcome.Should().Be(PriorAuthRuleImportOutcome.Written);
        report.Import.RuleSet.Should().Be("tenant-a/CA/Medicaid/any");
        report.Import.Rules.Select(r => r.RuleId).Should().Equal(
            "import:medi-cal:non-benefit-60000-thru-69999",
            "import:medi-cal:tar-60000-thru-69999");
        report.Import.Rules.SelectMany(r => r.Codes).Should().BeEquivalentTo("60650", "60660", "60661", "64999", "0721T");
        report.Import.Skipped.Should().ContainKey("the PA-required column says no");

        var enabled = (await repo.ListAsync(Tenant, "CA")).Where(r => r.IsEnabled).Select(r => r.RuleId);
        enabled.Should().Contain("import:knee");
    }

    [Fact]
    public async Task Non_benefit_codes_can_be_left_out_and_a_zip_is_read()
    {
        var repo = new PriorAuthRuleImportServiceTests.FakeRepo();
        var svc = new MediCalTarImportService(new PriorAuthRuleImportService(repo, NullLogger<PriorAuthRuleImportService>.Instance));
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = archive.CreateEntry("tar/tarandnoncd6.pdf").Open();
            await Pdf(Cd6).CopyToAsync(entry);
        }
        zip.Position = 0;

        var report = await svc.ImportAsync([("tar.zip", zip)],
            new MediCalTarImportOptions { NonBenefitNeedsTar = false, IncludeCodes = true }, Tenant, "admin");

        report.Import.Outcome.Should().Be(PriorAuthRuleImportOutcome.WouldWrite);
        report.Import.Rules.SelectMany(r => r.Codes).Should().BeEquivalentTo("60650", "60660", "60661");
        report.Warnings.Should().Contain(w => w.Contains("Non-Benefit codes are not loaded"));
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_non_pdf_upload_is_a_format_error()
    {
        var svc = new MediCalTarImportService(new PriorAuthRuleImportService(
            new PriorAuthRuleImportServiceTests.FakeRepo(), NullLogger<PriorAuthRuleImportService>.Instance));

        var act = () => svc.ImportAsync([("list.csv", new MemoryStream("Code\n60650\n"u8.ToArray()))],
            new MediCalTarImportOptions(), Tenant, "admin");

        await act.Should().ThrowAsync<PriorAuthRuleFormatException>();
    }

    /// <summary>A one-page PDF with each line at its own baseline, like the manual's text layer.</summary>
    private static MemoryStream Pdf(IEnumerable<string> lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var y = 800.0;
        foreach (var line in lines)
        {
            // Helvetica (WinAnsi) has no ‹ ›: write the manual's ASCII fallback.
            page.AddText(line.Replace("‹‹", "<<").Replace("››", ">>").Replace("–", "-"), 7, new PdfPoint(20, y), font);
            y -= 12;
        }
        return new MemoryStream(builder.Build());
    }
}
