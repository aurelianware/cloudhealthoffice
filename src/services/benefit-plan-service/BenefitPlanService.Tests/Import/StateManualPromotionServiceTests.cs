using System.Reflection;
using System.Text;
using System.Text.Json;
using BenefitPlanService.Controllers;
using BenefitPlanService.Import.PriorAuthRules;
using BenefitPlanService.Import.PriorAuthRules.Tmppm;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// The state Medicaid manual's extracted PA rules (TMPPM) promoted into the
/// tenant's PA rule set: which manual rules become rules, the tmppm namespace
/// that keeps them beside the plan's own list, re-promotion after a new
/// edition, and the controller's scope checks and outage answer.
/// </summary>
public sealed class StateManualPromotionServiceTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateOnly AsOf = new(2026, 11, 1);

    private static StateManualPaRule Manual(string id, bool auth = true, string type = "AuthRequired", params string[] codes) => new()
    {
        RuleId = id, State = "TX", Category = "Category " + id, TmppmRef = "§" + id, RuleType = type,
        ProcedureCodes = codes.ToList(), AuthRequired = auth,
        EffectiveDate = new DateOnly(2026, 4, 1), SourceEdition = "2026-10",
    };

    private sealed class FakeSource(params StateManualPaRule[] rules) : IStateManualRuleSource
    {
        public List<StateManualPaRule> Rules { get; } = rules.ToList();
        public Task<IReadOnlyList<StateManualPaRule>> GetRulesAsync(string tenantId, string state, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StateManualPaRule>>(Rules.Where(r => r.State == state).ToList());
    }

    private static (StateManualPromotionService Svc, PriorAuthRuleImportService Importer, PriorAuthRuleImportServiceTests.FakeRepo Repo)
        Build(FakeSource source)
    {
        var repo = new PriorAuthRuleImportServiceTests.FakeRepo();
        var importer = new PriorAuthRuleImportService(repo, NullLogger<PriorAuthRuleImportService>.Instance);
        return (new StateManualPromotionService(source, importer), importer, repo);
    }

    private static StateManualPromotionOptions Options(bool dryRun = true) =>
        new() { StateCode = "TX", Program = "STAR", DryRun = dryRun, AsOf = AsOf };

    [Fact]
    public async Task Only_current_auth_required_rules_with_codes_are_promoted()
    {
        var withDx = Manual("TX-9-2-46", true, "AuthRequired", "99500") with { AllowedDiagnoses = ["Z9981"] };
        var source = new FakeSource(
            Manual("TX-2-1", true, "AuthRequired", "70551", "70552"),
            withDx,
            Manual("TX-3-1", false, "AuthRequired", "99213"),
            Manual("TX-4-1", true, "AuthRequired"),
            Manual("TX-5-1", true, "Noncovered", "S9999"),
            Manual("TX-6-1", true, "AuthRequired", "27447") with { EndDate = new DateOnly(2026, 6, 30) },
            Manual("TX-7-1", true, "AuthRequired", "27130") with { EffectiveDate = new DateOnly(2027, 1, 1) });
        var (svc, _, repo) = Build(source);

        var report = await svc.PromoteAsync(Options(), Tenant, "admin");

        report.ManualRulesRead.Should().Be(7);
        report.ManualRulesPromoted.Should().Be(2);
        report.ManualRulesLeftOut.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["manual does not say authorization is required"] = 1,
            ["no procedure codes"] = 1,
            ["not covered (not a PA rule)"] = 1,
            ["ended"] = 1,
            ["not yet in effect"] = 1,
        });
        report.ConditionsNotApplied.Should().ContainSingle().Which.Should().StartWith("TX-9-2-46");
        report.Editions.Should().Equal("2026-10");
        report.Import.Outcome.Should().Be(PriorAuthRuleImportOutcome.WouldWrite);
        report.Import.RuleSet.Should().Be("tenant-a/TX/Medicaid/STAR");
        report.Import.Rules.Select(r => r.RuleId).Should().Equal("import:tmppm:tx-2-1", "import:tmppm:tx-9-2-46");
        report.Import.Rules.Should().OnlyContain(r => r.Condition == PaRequirementCondition.Always);
        report.Import.Rules[0].Name.Should().Be("§TX-2-1 Category TX-2-1");
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Promotion_sits_beside_the_plans_own_list_and_a_new_edition_disables_dropped_sections()
    {
        var source = new FakeSource(
            Manual("TX-2-1", true, "AuthRequired", "70551"),
            Manual("TX-2-2", true, "AuthRequired", "72148"));
        var (svc, importer, repo) = Build(source);

        // The plan's own list in the same scope.
        var own = await importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes("Category,Code\nKnee,27447\n")),
            new PriorAuthRuleImportOptions { StateCode = "TX", LineOfBusiness = PaLineOfBusiness.Medicaid, Program = "STAR", DryRun = false },
            Tenant, "admin");
        own.Outcome.Should().Be(PriorAuthRuleImportOutcome.Written);

        (await svc.PromoteAsync(Options(dryRun: false), Tenant, "admin")).Import.Outcome.Should().Be(PriorAuthRuleImportOutcome.Written);
        Enabled(repo).Should().Equal("import:knee", "import:tmppm:tx-2-1", "import:tmppm:tx-2-2");

        // Next edition drops §TX-2-2: only the manual's rule is disabled.
        source.Rules.RemoveAll(r => r.RuleId == "TX-2-2");
        var again = await svc.PromoteAsync(Options(dryRun: false), Tenant, "admin");
        again.Import.RulesDisabled.Should().Equal("import:tmppm:tx-2-2");
        Enabled(repo).Should().Equal("import:knee", "import:tmppm:tx-2-1");

        // Re-importing the plan's own list leaves the manual's rules alone.
        var ownAgain = await importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes("Category,Code\nHip,27130\n")),
            new PriorAuthRuleImportOptions { StateCode = "TX", LineOfBusiness = PaLineOfBusiness.Medicaid, Program = "STAR", DryRun = false },
            Tenant, "admin");
        ownAgain.RulesDisabled.Should().Equal("import:knee");
        Enabled(repo).Should().Equal("import:hip", "import:tmppm:tx-2-1");
    }

    [Fact]
    public async Task No_manual_rules_writes_nothing_and_says_to_run_the_ingestion()
    {
        var (svc, _, repo) = Build(new FakeSource());

        var report = await svc.PromoteAsync(Options(dryRun: false), Tenant, "admin");

        report.Import.Outcome.Should().Be(PriorAuthRuleImportOutcome.NothingToImport);
        report.Warnings.Should().ContainSingle(w => w.Contains("ingestion tool"));
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Conditions_in_json_are_noted_and_large_sections_are_flagged()
    {
        var codes = Enumerable.Range(10000, StateManualPromotionService.LargeRuleCodeCount + 1).Select(i => i.ToString()).ToArray();
        var age = JsonDocument.Parse("""{"maxAge":20,"unit":"years"}""").RootElement;
        var source = new FakeSource(
            Manual("TX-8-1", true, "AuthRequired", codes),
            Manual("TX-8-2", true, "AuthRequired", "E0260") with { AgeLimit = age, SourceEdition = "2026-04" });
        var (svc, _, _) = Build(source);

        var report = await svc.PromoteAsync(Options(), Tenant, "admin");

        report.ConditionsNotApplied.Should().ContainSingle().Which.Should().StartWith("TX-8-2");
        report.Warnings.Should().Contain(w => w.Contains("more than 200 codes") && w.Contains("TX-8-1"));
        report.Warnings.Should().Contain(w => w.Contains("2 editions"));
        report.Editions.Should().Equal("2026-10", "2026-04");
    }

    [Fact]
    public async Task Unknown_namespace_is_refused()
    {
        var (_, importer, _) = Build(new FakeSource());
        var act = () => importer.ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes("Code\n70551\n")),
            new PriorAuthRuleImportOptions { StateCode = "TX", LineOfBusiness = PaLineOfBusiness.Medicaid, RuleNamespace = "other" },
            Tenant, "admin");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Endpoint_needs_settings_manage()
    {
        typeof(PriorAuthRulesController).GetMethod(nameof(PriorAuthRulesController.ImportStateManual))!
            .GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission).Should().Equal("settings:manage");
    }

    [Theory]
    [InlineData("Texas", PaLineOfBusiness.Medicaid, null)]
    [InlineData("TX", (PaLineOfBusiness)0, null)]
    [InlineData("TX", PaLineOfBusiness.Medicaid, "../STAR")]
    public async Task Endpoint_rejects_bad_scope(string state, PaLineOfBusiness lob, string? program)
    {
        var promotion = new Mock<IStateManualPromotionService>(MockBehavior.Strict);
        var controller = Controller(promotion.Object);

        (await controller.ImportStateManual(state, lob, program)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Endpoint_answers_503_when_terminology_service_cannot_be_read()
    {
        var promotion = new Mock<IStateManualPromotionService>();
        promotion.Setup(p => p.PromoteAsync(It.IsAny<StateManualPromotionOptions>(), "tenant-a", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StateManualRuleSourceException("down"));

        var result = await Controller(promotion.Object).ImportStateManual();

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
    }

    private static PriorAuthRulesController Controller(IStateManualPromotionService promotion)
    {
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.TenantId).Returns("tenant-a");
        return new PriorAuthRulesController(
            new Mock<CloudHealthOffice.PriorAuthRuleEngine.Persistence.IPaRuleRepository>().Object, actor.Object,
            NullLogger<PriorAuthRulesController>.Instance, importer: null, manualPromotion: promotion);
    }

    private static List<string> Enabled(PriorAuthRuleImportServiceTests.FakeRepo repo) =>
        repo.ListAsync(Tenant, "TX").Result.Where(r => r.IsEnabled).Select(r => r.RuleId)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
}
