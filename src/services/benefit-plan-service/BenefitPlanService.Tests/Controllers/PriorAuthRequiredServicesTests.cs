using System.Reflection;
using BenefitPlanService.Controllers;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>CMS-0057-F list of PA-required items and services (GET api/v1/prior-auth-rules/required-services).</summary>
public class PriorAuthRequiredServicesTests
{
    private readonly Mock<IPaRuleRepository> _repo = new();
    private readonly Mock<ICurrentActor> _actor = new();

    public PriorAuthRequiredServicesTests()
    {
        _actor.SetupGet(a => a.TenantId).Returns("tenant-a");
        _repo.Setup(r => r.GetRulesAsync(It.IsAny<RuleSetKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PaRuleDocument>());
    }

    private PriorAuthRulesController Controller() =>
        new(_repo.Object, _actor.Object, NullLogger<PriorAuthRulesController>.Instance);

    private void TenantRule(params string[] codes) =>
        _repo.Setup(r => r.GetRulesAsync(It.Is<RuleSetKey>(k => k.TenantId == "tenant-a" && k.Program == "STAR"),
            It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new PaRuleDocument {
                RuleId = "T-1", RuleName = "Knee replacement", StateCode = "TX", Lob = PaLineOfBusiness.Medicaid,
                Program = "STAR", TenantId = "tenant-a", Category = RuleCategory.ClinicalCriteria, Scope = RuleScope.Tenant,
                Priority = 10, RuleType = "ProcedureRequiresAuth", ProcedureCodes = codes } });

    [Fact]
    public async Task Lists_the_callers_own_rule_set()
    {
        TenantRule("27447");
        var ok = (await Controller().RequiredServices("tx", PaLineOfBusiness.Medicaid, "STAR", null, default))
            .Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeOfType<PaRequiredServicesList>().Subject;
        list.StateCode.Should().Be("TX");
        list.RuleSet.Should().Be("tenant-a/TX/Medicaid/STAR");
        list.Entries.Single().Codes.Should().Equal("27447");
        _repo.Verify(r => r.GetRulesAsync(It.Is<RuleSetKey>(k => k.TenantId != null && k.TenantId != "tenant-a"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Csv_is_returned_as_a_file()
    {
        TenantRule("27447");
        var file = (await Controller().RequiredServices("TX", PaLineOfBusiness.Medicaid, "STAR", "csv", default))
            .Should().BeOfType<FileContentResult>().Subject;
        file.ContentType.Should().Be("text/csv");
        file.FileDownloadName.Should().Be("prior-auth-required-services-tx-medicaid-star.csv");
        System.Text.Encoding.UTF8.GetString(file.FileContents).Should().Contain("T-1,Knee replacement,27447");
    }

    [Theory]
    [InlineData("Texas", PaLineOfBusiness.Medicaid, null, null)]
    [InlineData("TX", (PaLineOfBusiness)0, null, null)]
    [InlineData("TX", PaLineOfBusiness.Medicaid, "../STAR", null)]
    [InlineData("TX", PaLineOfBusiness.Medicaid, null, "xml")]
    public async Task Rejects_bad_scope(string state, PaLineOfBusiness lob, string? program, string? format)
    {
        (await Controller().RequiredServices(state, lob, program, format, default)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public void Needs_benefit_read_permission()
    {
        typeof(PriorAuthRulesController).GetMethod(nameof(PriorAuthRulesController.RequiredServices))!
            .GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission).Should().Equal("benefits:read");
    }

    [Fact]
    public void Import_needs_settings_manage_permission()
    {
        typeof(PriorAuthRulesController).GetMethod(nameof(PriorAuthRulesController.ImportCsv))!
            .GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission).Should().Equal("settings:manage");
    }

    [Theory]
    [InlineData("Texas", PaLineOfBusiness.Medicaid, null)]
    [InlineData("TX", (PaLineOfBusiness)0, null)]
    [InlineData("TX", PaLineOfBusiness.Medicaid, "../STAR")]
    public async Task Import_rejects_bad_scope(string state, PaLineOfBusiness lob, string? program)
    {
        var importer = new Mock<BenefitPlanService.Import.PriorAuthRules.IPriorAuthRuleImportService>(MockBehavior.Strict);
        var controller = new PriorAuthRulesController(_repo.Object, _actor.Object,
            NullLogger<PriorAuthRulesController>.Instance, importer.Object);
        var bytes = System.Text.Encoding.UTF8.GetBytes("Code\n70551\n");
        var file = new Microsoft.AspNetCore.Http.FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "list.csv");

        (await controller.ImportCsv(file, state, lob, program)).Should().BeOfType<BadRequestObjectResult>();
    }
}
