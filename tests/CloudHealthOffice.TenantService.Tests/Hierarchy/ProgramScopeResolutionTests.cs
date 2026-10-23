using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.TenantService.Tests.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Config = TenantService.Configuration;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// Resolving through the levels programs bring (plan-configuration.md §3):
/// carrier, program and benefit plan, and what a program record fills in for a
/// caller that holds only a program id.
/// </summary>
public class ProgramScopeResolutionTests
{
    private static readonly DateTime Jun1 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private int _version;

    /// <summary>A day count settable at every level the hierarchy has.</summary>
    private static readonly SettingDefinition Days = new()
    {
        Key = "test.days",
        Type = SettingType.Integer,
        Owner = "test",
        Description = "Days",
        Default = "30",
        DefaultsByLineOfBusiness = new Dictionary<string, string> { [LinesOfBusiness.Medicaid] = "0" },
        AllowedLevels = new HashSet<SettingLevel>
        {
            SettingLevel.Tenant, SettingLevel.Carrier, SettingLevel.LineOfBusiness,
            SettingLevel.Program, SettingLevel.BenefitPlan, SettingLevel.Sponsor,
        },
    };

    private static readonly ProgramDescriptor TexasStar = new()
    {
        Id = "t1|DEFAULT|STAR",
        Code = "STAR",
        Name = "Texas STAR",
        CarrierId = "t1|DEFAULT",
        LineOfBusiness = LinesOfBusiness.Medicaid,
        StateCode = "TX",
    };

    private SettingValue V(SettingLevel level, string scope, string value, bool locked = false) => new()
    {
        TenantId = "t1", Key = Days.Key, Level = level, ScopeId = scope, Value = value, Locked = locked,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Version = ++_version,
    };

    private static ResolvedSetting Resolve(SettingScope scope, params SettingValue[] values) =>
        SettingResolver.Resolve(Days, scope, Jun1, values);

    [Fact]
    public void The_chain_runs_from_the_sponsor_down_to_the_tenant()
    {
        var scope = new SettingScope
        {
            Sponsor = "ACME", BenefitPlan = "PLAN-1", Program = TexasStar.Id,
            LineOfBusiness = LinesOfBusiness.Medicaid, Carrier = TexasStar.CarrierId,
        };

        scope.Chain().Select(c => c.Level).Should().Equal(
            SettingLevel.Sponsor, SettingLevel.BenefitPlan, SettingLevel.Program,
            SettingLevel.LineOfBusiness, SettingLevel.Carrier, SettingLevel.Tenant);
    }

    [Fact]
    public void A_program_overrides_its_line_of_business_and_a_plan_overrides_its_program()
    {
        var scope = new SettingScope { Program = TexasStar.Id, BenefitPlan = "PLAN-1" }.CompletedFrom(TexasStar);
        var tenant = V(SettingLevel.Tenant, "", "40");
        var carrier = V(SettingLevel.Carrier, TexasStar.CarrierId, "35");
        var lob = V(SettingLevel.LineOfBusiness, LinesOfBusiness.Medicaid, "20");
        var program = V(SettingLevel.Program, TexasStar.Id, "10");
        var plan = V(SettingLevel.BenefitPlan, "PLAN-1", "5");

        Resolve(scope, tenant, carrier, lob, program, plan).Should().Match<ResolvedSetting>(
            r => r.Value == "5" && r.SourceLevel == SettingLevel.BenefitPlan);
        Resolve(scope with { BenefitPlan = null }, tenant, carrier, lob, program, plan).Should().Match<ResolvedSetting>(
            r => r.Value == "10" && r.SourceLevel == SettingLevel.Program);
        Resolve(scope with { BenefitPlan = null, Program = null }, tenant, carrier, lob, program, plan).Should().Match<ResolvedSetting>(
            r => r.Value == "20" && r.SourceLevel == SettingLevel.LineOfBusiness);
        Resolve(new SettingScope { Carrier = TexasStar.CarrierId }, tenant, carrier, lob, program, plan).Should().Match<ResolvedSetting>(
            r => r.Value == "35" && r.SourceLevel == SettingLevel.Carrier);
    }

    [Fact]
    public void A_program_id_alone_resolves_its_carrier_and_line_of_business()
    {
        // The reason to have programs: one id places the member on the whole branch.
        var scope = new SettingScope { Program = TexasStar.Id }.CompletedFrom(TexasStar);

        scope.Carrier.Should().Be("t1|DEFAULT");
        scope.LineOfBusiness.Should().Be(LinesOfBusiness.Medicaid);
        Resolve(scope).Value.Should().Be("0", "the Medicaid default, which the program's line of business selects");
        Resolve(scope, V(SettingLevel.Carrier, "t1|DEFAULT", "35")).Value.Should().Be("35");
    }

    [Fact]
    public void What_the_caller_already_knows_is_not_overwritten_by_the_program()
    {
        var scope = new SettingScope { Program = TexasStar.Id, Carrier = "t1|INS", LineOfBusiness = LinesOfBusiness.Commercial }
            .CompletedFrom(TexasStar);

        scope.Carrier.Should().Be("t1|INS");
        scope.LineOfBusiness.Should().Be(LinesOfBusiness.Commercial);
    }

    [Fact]
    public void A_lock_at_the_program_stops_a_plan_or_a_sponsor_overriding_it()
    {
        var scope = new SettingScope { Program = TexasStar.Id, BenefitPlan = "PLAN-1", Sponsor = "ACME" }.CompletedFrom(TexasStar);

        var r = Resolve(scope,
            V(SettingLevel.Program, TexasStar.Id, "0", locked: true),
            V(SettingLevel.BenefitPlan, "PLAN-1", "30"),
            V(SettingLevel.Sponsor, "ACME", "90"));

        r.Value.Should().Be("0");
        r.Locked.Should().BeTrue();
        r.SourceLevel.Should().Be(SettingLevel.Program);
        r.Trace.Where(t => t.Level > SettingLevel.Program).Should().OnlyContain(t => t.Note.StartsWith("Ignored: locked at Program"));
    }

    [Fact]
    public void A_setting_not_allowed_at_a_level_ignores_a_value_stored_there()
    {
        var tenantOnly = Days with { AllowedLevels = new HashSet<SettingLevel> { SettingLevel.Tenant } };
        var scope = new SettingScope { Program = TexasStar.Id }.CompletedFrom(TexasStar);

        SettingResolver.Resolve(tenantOnly, scope, Jun1, [V(SettingLevel.Program, TexasStar.Id, "10"), V(SettingLevel.Tenant, "", "40")])
            .Should().Match<ResolvedSetting>(r => r.Value == "40" && r.SourceLevel == SettingLevel.Tenant);
    }

    [Fact]
    public void The_cache_key_separates_two_members_on_different_programs()
    {
        var star = new SettingScope { Program = "t1|DEFAULT|STAR", LineOfBusiness = LinesOfBusiness.Medicaid };
        var starKids = star with { Program = "t1|DEFAULT|STARKIDS" };

        star.CacheKey.Should().NotBe(starKids.CacheKey);
        star.CacheKey.Should().NotBe((star with { BenefitPlan = "PLAN-1" }).CacheKey);
    }

    [Fact]
    public void A_write_at_a_program_or_plan_needs_product_configuration_and_a_carrier_needs_enterprise()
    {
        SettingValidator.WritePermissionFor(SettingLevel.Program).Should().Be(PlanConfigurationPermissions.Product);
        SettingValidator.WritePermissionFor(SettingLevel.BenefitPlan).Should().Be(PlanConfigurationPermissions.Product);
        SettingValidator.WritePermissionFor(SettingLevel.Carrier).Should().Be(PlanConfigurationPermissions.Enterprise);
    }

    [Fact]
    public async Task A_program_level_write_is_stored_and_read_back_at_that_node()
    {
        var store = new InMemoryConfigurationStore();
        var clock = new FixedClock(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = new Config.ConfigurationService(
            new TestCatalog(Days), store, new RecordingPublisher(), clock,
            NullLogger<Config.ConfigurationService>.Instance);
        var analyst = HierarchyTestKit.Analyst();

        var write = await service.WriteAsync(analyst, Days.Key, SettingLevel.Program, TexasStar.Id,
            new Config.SettingWrite { Value = "0" }, clear: false);

        write.Outcome.Should().Be(Config.ConfigurationOutcome.Applied);
        var resolved = await service.ResolveAsync("t1", Days.Key,
            new SettingScope { Program = TexasStar.Id }.CompletedFrom(TexasStar), asOf: null);
        resolved!.Value.Should().Be("0");
        resolved.SourceLevel.Should().Be(SettingLevel.Program);
    }

    [Fact]
    public async Task A_carrier_level_write_is_refused_to_a_product_analyst()
    {
        var store = new InMemoryConfigurationStore();
        var clock = new FixedClock(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var service = new Config.ConfigurationService(
            new TestCatalog(Days), store, new RecordingPublisher(), clock,
            NullLogger<Config.ConfigurationService>.Instance);

        var result = await service.WriteAsync(HierarchyTestKit.Analyst(), Days.Key, SettingLevel.Carrier, "t1|DEFAULT",
            new Config.SettingWrite { Value = "35" }, clear: false);

        result.Outcome.Should().Be(Config.ConfigurationOutcome.Forbidden);
    }
}
