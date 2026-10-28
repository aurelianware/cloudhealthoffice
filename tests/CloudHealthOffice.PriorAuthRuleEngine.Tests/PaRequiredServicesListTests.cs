using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.SeedRules;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using FluentAssertions;
using NSubstitute;

namespace CloudHealthOffice.PriorAuthRuleEngine.Tests;

public class PaRequiredServicesListTests
{
    private static readonly DateTime At = new(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    private static PaRuleDocument Rule(string id, string type, RuleCategory category = RuleCategory.ClinicalCriteria,
        string? tenant = null, string? program = "STAR") => new()
    {
        RuleId = id, RuleName = id + " name", StateCode = "TX", Lob = PaLineOfBusiness.Medicaid, Program = program,
        TenantId = tenant, Category = category, Scope = tenant is null ? RuleScope.Platform : RuleScope.Tenant,
        Priority = 10, RuleType = type,
    };

    private static IReadOnlyList<PaRuleDocument> Seed(string program) =>
        TxMedicaidSeedRules.GetAll().Where(r => r.Program == program).ToList();

    [Fact]
    public void Star_seed_lists_inpatient_services_and_therapy_limits_with_their_exemptions()
    {
        var list = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STAR", null, Seed("STAR"), At);

        var inpatient = list.Entries.Single(e => e.RuleId == "TX-STAR-PA-001");
        inpatient.AllServices.Should().BeTrue();
        inpatient.Condition.Should().Be(PaRequirementCondition.Always);
        inpatient.PlacesOfService.Should().Equal("21");

        var chiro = list.Entries.Single(e => e.RuleId == "TX-STAR-QTY-001");
        chiro.Condition.Should().Be(PaRequirementCondition.AboveQuantityLimit);
        chiro.Codes.Should().Equal("98940", "98941", "98942", "98943");
        (chiro.UnitLimit ?? chiro.VisitLimit).Should().NotBeNull();

        // The retired (disabled) gold-card seed is not an exemption anyone gets.
        list.Exemptions.Select(x => x.RuleId).Should().Equal("TX-STAR-PCP-001");
        list.Entries.Should().NotContain(e => e.RuleId == "TX-STAR-REG-001" || e.RuleId == "TX-STAR-PCP-001");
        list.UnclassifiedRuleIds.Should().BeEmpty();
    }

    [Fact]
    public void StarPlus_seed_lists_dme_ranges_and_the_diagnosis_rule()
    {
        var list = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STARPlus", null, Seed("STARPlus"), At);
        list.Entries.Single(e => e.RuleId == "TX-STARPLUS-PA-001").CodePrefixes.Should().Equal("A", "E", "K");
        var dx = list.Entries.Single(e => e.RuleId == "TX-STARPLUS-DX-001");
        dx.Condition.Should().Be(PaRequirementCondition.WithDiagnosis);
        dx.Diagnoses.Should().NotBeEmpty();
    }

    [Fact]
    public void Drugs_are_excluded_and_reported_separately()
    {
        var mixed = Rule("MIXED", "ProcedureRequiresAuth"); mixed.ProcedureCodes = ["J1745", "27447"];
        var drugsOnly = Rule("DRUGS", "ProcedureRequiresAuth"); drugsOnly.ProcedureCodePrefixes = ["J"];
        var list = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STAR", null, [mixed, drugsOnly], At);

        list.Entries.Single().Codes.Should().Equal("27447");
        list.ExcludedDrugCodes.Should().Equal("J", "J1745");
    }

    [Fact]
    public void Disabled_limitless_and_unknown_rules_are_not_listed_but_unknown_ones_are_flagged()
    {
        var disabled = Rule("OFF", "ProcedureRequiresAuth"); disabled.IsEnabled = false; disabled.ProcedureCodes = ["27447"];
        var limitless = Rule("QTY", "QuantityLimit", RuleCategory.QuantityLimit); limitless.ProcedureCodes = ["97110"];
        var unknown = Rule("NEW", "SomeFutureRule");
        var denied = Rule("NC", "ProcedureRequiresAuth"); denied.ProcedureCodes = ["S9999"]; denied.DenialCode = "96";

        var list = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STAR", null,
            [disabled, limitless, unknown, denied], At);

        list.Entries.Select(e => e.RuleId).Should().Equal("NC");
        list.Entries.Single().DeniedWithoutReview.Should().BeTrue();
        list.UnclassifiedRuleIds.Should().Equal("NEW");
    }

    [Fact]
    public void Csv_has_one_row_per_entry_and_neutralises_formulas()
    {
        var rule = Rule("R1", "ProcedureRequiresAuth"); rule.RuleName = "=HYPERLINK(\"x\"), knee"; rule.ProcedureCodes = ["27447", "27446"];
        var csv = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STAR", null, [rule], At).ToCsv();
        var lines = csv.TrimEnd().Split(Environment.NewLine);
        lines.Should().HaveCount(2);
        lines[1].Should().StartWith("R1,\"'=HYPERLINK(\"\"x\"\"), knee\",27446 27447,,false,Always,");
    }

    [Fact]
    public async Task Resolver_uses_the_tenant_program_set_before_the_platform_one()
    {
        var repo = Substitute.For<IPaRuleRepository>();
        var tenantRule = Rule("T1", "ProcedureRequiresAuth", tenant: "tenant-a");
        var platformRule = Rule("P1", "ProcedureRequiresAuth");
        repo.GetRulesAsync(Arg.Is<RuleSetKey>(k => k.TenantId == "tenant-a" && k.Program == "STAR"), Arg.Any<CancellationToken>())
            .Returns([tenantRule]);
        repo.GetRulesAsync(Arg.Is<RuleSetKey>(k => k.TenantId == null && k.Program == "STAR"), Arg.Any<CancellationToken>())
            .Returns([platformRule]);

        var (key, rules) = await PaRuleSetResolver.ResolveAsync(repo, "TX", PaLineOfBusiness.Medicaid, "STAR", "tenant-a", null, default);
        key!.TenantId.Should().Be("tenant-a");
        rules.Select(r => r.RuleId).Should().Equal("T1");

        var (platformKey, platformRules) = await PaRuleSetResolver.ResolveAsync(repo, "TX", PaLineOfBusiness.Medicaid, "STAR", "tenant-b", null, default);
        platformKey!.TenantId.Should().BeNull();
        platformRules.Select(r => r.RuleId).Should().Equal("P1");
    }
}
