using CloudHealthOffice.PlanConfiguration;
using TenantService.Configuration;

namespace CloudHealthOffice.TenantService.Tests.Configuration;

/// <summary>
/// "Where is this overridden?" confined to one branch of the hierarchy
/// (plan-configuration.md §12.6). Before step 2 the answer at every level
/// below the node was every node in the tenant, which read as if those
/// overrides applied to the node asked about.
///
/// <para>
/// A carrier reaches its programs, and through them their lines of business
/// and benefit plans. It does not reach sponsors: the sponsor contract and its
/// policies and classes arrive with step 3, so a sponsor-level node is still
/// listed whatever branch it belongs to — and the response says so, rather
/// than letting a caller read an unnarrowed level as a narrowed one. The same
/// is said, with a different reason, when the hierarchy could not be read at
/// all: an outage must not quietly shrink the list to nothing.
/// </para>
/// </summary>
public class OverrideNarrowingTests
{
    private const string Tenant = "t1";
    private static readonly string Key = TestSettings.Anywhere.Key;

    private readonly InMemoryConfigurationStore _store = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly FixedClock _clock = new(new DateTime(2026, 10, 23, 15, 0, 0, DateTimeKind.Utc));

    private readonly FakeDescendants _hierarchy = new()
    {
        // Two commercial programs under one carrier, a Medicaid one under another.
        Programs = { ["P1"] = ("C1", "Commercial"), ["P2"] = ("C1", "Commercial"), ["P3"] = ("C2", "Medicaid") },
        Plans = { ["PL1"] = "P1", ["PL3"] = "P3" },
    };

    private ConfigurationService Service(IHierarchyDescendants? descendants) =>
        TestSettings.Service(_store, _publisher, _clock, descendants: descendants);

    private ConfigurationService Narrowing => Service(_hierarchy);

    public OverrideNarrowingTests()
    {
        Set(SettingLevel.Carrier, "C1");
        Set(SettingLevel.Carrier, "C2");
        Set(SettingLevel.LineOfBusiness, "Commercial");
        Set(SettingLevel.LineOfBusiness, "Medicaid");
        Set(SettingLevel.Program, "P1");
        Set(SettingLevel.Program, "P3");
        Set(SettingLevel.BenefitPlan, "PL1");
        Set(SettingLevel.BenefitPlan, "PL3");
        Set(SettingLevel.Sponsor, "ACME");
    }

    private void Set(SettingLevel level, string scopeId) => _store.AppendAsync(new SettingValue
    {
        TenantId = Tenant, Key = Key, Level = level, ScopeId = scopeId, Value = "1",
        EffectiveFrom = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc), SetBy = "test", SetAt = _clock.Now,
    }).GetAwaiter().GetResult();

    private static IEnumerable<(SettingLevel, string)> Nodes(SettingOverrides answer) =>
        answer.Overrides.Select(o => (o.Level, o.ScopeId));

    [Fact]
    public async Task A_carriers_overrides_are_its_own_programs_their_lines_and_their_plans()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Carrier, "C1", null))!;

        Nodes(answer).Should().Equal(
            (SettingLevel.Carrier, "C1"),
            (SettingLevel.LineOfBusiness, "Commercial"),
            (SettingLevel.Program, "P1"),
            (SettingLevel.BenefitPlan, "PL1"),
            // The other carrier's sponsor cannot be told from this one's yet.
            (SettingLevel.Sponsor, "ACME"));
        answer.Narrowed.Should().Equal(SettingLevel.LineOfBusiness, SettingLevel.Program, SettingLevel.BenefitPlan);
    }

    [Fact]
    public async Task A_programs_overrides_are_its_own_plans()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Program, "P3", null))!;

        Nodes(answer).Should().Equal(
            (SettingLevel.Program, "P3"),
            (SettingLevel.BenefitPlan, "PL3"),
            (SettingLevel.Sponsor, "ACME"));
        answer.Narrowed.Should().Equal(SettingLevel.BenefitPlan);
    }

    [Fact]
    public async Task A_line_of_business_reaches_the_programs_of_every_carrier_that_runs_it()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.LineOfBusiness, "Medicaid", null))!;

        Nodes(answer).Should().Equal(
            (SettingLevel.LineOfBusiness, "Medicaid"),
            (SettingLevel.Program, "P3"),
            (SettingLevel.BenefitPlan, "PL3"),
            (SettingLevel.Sponsor, "ACME"));
        answer.Narrowed.Should().Equal(SettingLevel.Program, SettingLevel.BenefitPlan);
    }

    [Fact]
    public async Task Sponsor_policies_and_classes_are_named_as_deferred_rather_than_left_to_be_inferred()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Carrier, "C1", null))!;

        answer.Unnarrowed.Select(u => u.Level).Should().Equal(SettingLevel.Sponsor, SettingLevel.SponsorPolicy, SettingLevel.Class);
        answer.Unnarrowed.Should().OnlyContain(u => u.Reason == ConfigurationService.SponsorsAwaitStepThree);
        // At sponsor level and below, every node in the tenant is listed; the
        // reason is what tells a caller that, so it must not be empty.
        answer.Unnarrowed.Should().OnlyContain(u => u.Reason.Length > 0);
    }

    [Fact]
    public async Task A_sponsors_own_overrides_are_not_narrowed_either()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Sponsor, "ACME", null))!;

        Nodes(answer).Should().Equal((SettingLevel.Sponsor, "ACME"));
        answer.Narrowed.Should().BeEmpty();
        answer.Unnarrowed.Select(u => u.Level).Should().Equal(SettingLevel.SponsorPolicy, SettingLevel.Class);
    }

    [Fact]
    public async Task The_whole_tenant_is_its_own_branch_and_costs_no_hierarchy_read()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Tenant, null, null))!;

        answer.Overrides.Should().HaveCount(9, "every override in the tenant is under the tenant");
        answer.Narrowed.Should().BeEmpty();
        answer.Unnarrowed.Should().BeEmpty();
        _hierarchy.ProgramReads.Should().Be(0);
    }

    [Fact]
    public async Task A_carrier_with_no_programs_has_no_overrides_below_it()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Carrier, "C3", null))!;

        // Nothing is set at C3 itself either, so only the sponsor is left.
        Nodes(answer).Should().Equal((SettingLevel.Sponsor, "ACME"));
        answer.Narrowed.Should().Equal(SettingLevel.LineOfBusiness, SettingLevel.Program, SettingLevel.BenefitPlan);
    }

    [Fact]
    public async Task A_hierarchy_that_could_not_be_read_leaves_the_list_whole_and_says_which_levels_are_not_narrowed()
    {
        _hierarchy.Fail = true;

        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Carrier, "C1", null))!;

        // The overrides below C1 are still all there: an outage that shrank the
        // list would read as "this carrier overrides nothing", which is the one
        // answer that must never come out of a failure.
        Nodes(answer).Should().Equal(
            (SettingLevel.Carrier, "C1"),
            (SettingLevel.LineOfBusiness, "Commercial"),
            (SettingLevel.LineOfBusiness, "Medicaid"),
            (SettingLevel.Program, "P1"),
            (SettingLevel.Program, "P3"),
            (SettingLevel.BenefitPlan, "PL1"),
            (SettingLevel.BenefitPlan, "PL3"),
            (SettingLevel.Sponsor, "ACME"));
        answer.Narrowed.Should().BeEmpty();
        answer.Unnarrowed.Should().Contain(u => u.Level == SettingLevel.Program && u.Reason == ConfigurationService.HierarchyUnreadable);
        answer.Unnarrowed.Should().Contain(u => u.Level == SettingLevel.Sponsor && u.Reason == ConfigurationService.SponsorsAwaitStepThree);
    }

    [Fact]
    public async Task Plans_that_could_not_be_read_give_up_the_narrowing_above_them_too()
    {
        // Programs read, plans did not. Narrowing the programs and not the plans
        // would answer with one carrier's programs beside another's plans, which
        // is harder to read correctly than an honest unnarrowed list.
        _hierarchy.FailPlans = true;

        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Carrier, "C1", null))!;

        answer.Narrowed.Should().BeEmpty();
        Nodes(answer).Should().Contain((SettingLevel.Program, "P3")).And.Contain((SettingLevel.BenefitPlan, "PL3"));
        answer.Unnarrowed.Where(u => u.Reason == ConfigurationService.HierarchyUnreadable).Select(u => u.Level)
            .Should().Equal(SettingLevel.LineOfBusiness, SettingLevel.Program, SettingLevel.BenefitPlan);
    }

    [Fact]
    public async Task A_host_with_no_hierarchy_reader_narrows_nothing_and_admits_it()
    {
        var answer = (await Service(null).OverridesAsync(Tenant, Key, SettingLevel.Carrier, "C1", null))!;

        answer.Narrowed.Should().BeEmpty();
        answer.Unnarrowed.Select(u => u.Level).Should().Equal(
            SettingLevel.Sponsor, SettingLevel.SponsorPolicy, SettingLevel.Class,
            SettingLevel.LineOfBusiness, SettingLevel.Program, SettingLevel.BenefitPlan);
        // Said differently from an outage: a host with no reader is how it was
        // built, and an outage is worth someone knowing about.
        answer.Unnarrowed.Should().Contain(u => u.Reason == ConfigurationService.NoHierarchyReader);
    }

    [Fact]
    public async Task The_node_asked_about_is_echoed_so_an_answer_can_be_read_on_its_own()
    {
        var answer = (await Narrowing.OverridesAsync(Tenant, Key, SettingLevel.Carrier, " C1 ", null))!;

        answer.Level.Should().Be(SettingLevel.Carrier);
        answer.ScopeId.Should().Be("C1", "the node is echoed as it was stored, not as it was typed");
    }

    [Fact]
    public async Task An_unknown_key_is_still_no_answer_at_all()
    {
        (await Narrowing.OverridesAsync(Tenant, "no.such.key", SettingLevel.Carrier, "C1", null)).Should().BeNull();
    }

    [Fact]
    public async Task One_tenants_branch_is_not_anothers()
    {
        (await Narrowing.OverridesAsync("t2", Key, SettingLevel.Carrier, "C1", null))!.Overrides.Should().BeEmpty();
    }
}
