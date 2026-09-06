using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The inventory is what keeps the harness honest about what has and has not been
/// proven. A placeholder must never look like a result.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ScenarioInventoryTests
{
    private static readonly InteropScenarioInventory Inventory = InteropScenarioInventory.Load();
    private static readonly InteropVersions Versions = InteropVersions.Load();

    [Theory]
    [InlineData("BR-CRD-001")]
    [InlineData("BR-DTR-001")]
    [InlineData("BR-PAS-SUBMIT-001")]
    [InlineData("BR-PAS-INQUIRE-001")]
    [InlineData("INFERNO-DTR-PAYER-001")]
    [InlineData("INFERNO-PDEX-SERVER-001")]
    [InlineData("INFERNO-PDEX-CLIENT-001")]
    public void The_planned_scenario_inventory_is_present(string scenarioId)
    {
        Inventory.Scenario(scenarioId).Should().NotBeNull();
    }

    [Fact]
    public void Only_the_scenarios_this_harness_actually_executes_are_marked_implemented()
    {
        // An inventory row is a plan, not a result. This list grows only when a
        // scenario that genuinely crosses into the external implementation lands,
        // so a placeholder can never present itself as proven.
        Inventory.Scenarios
            .Where(s => s.Implemented)
            .Select(s => s.Id)
            .Should().BeEquivalentTo(["BR-PAS-SUBMIT-001", "BR-CRD-001"]);
    }

    [Fact]
    public void Every_implemented_scenario_has_a_scenario_test_carrying_its_id()
    {
        // Guards the other direction: a row marked implemented with no test behind
        // it would be reported NotRun forever while claiming to be implemented.
        // TraitAttribute exposes nothing at runtime, so the ids are read from the
        // attribute's constructor arguments in metadata.
        var tested = typeof(InteropVersions).Assembly.GetTypes()
            .SelectMany(type => type.GetCustomAttributesData())
            .Where(data => data.AttributeType == typeof(TraitAttribute)
                           && data.ConstructorArguments.Count == 2
                           && (string?)data.ConstructorArguments[0].Value == "Scenario")
            .Select(data => (string?)data.ConstructorArguments[1].Value)
            .Where(id => id is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        foreach (var scenario in Inventory.Scenarios.Where(s => s.Implemented))
        {
            tested.Should().Contain(scenario.Id,
                "'{0}' is marked implemented, so a scenario test must carry [Trait(\"Scenario\", \"{0}\")]",
                scenario.Id);
        }
    }

    [Fact]
    public void Every_scenario_names_a_pinned_target()
    {
        foreach (var scenario in Inventory.Scenarios)
        {
            var act = () => Versions.Target(scenario.ExternalTarget);
            act.Should().NotThrow($"scenario '{scenario.Id}' names external target '{scenario.ExternalTarget}'");
        }
    }

    [Fact]
    public void Both_directional_roles_are_representable()
    {
        var roles = Inventory.Scenarios.Select(s => s.ParsedChoRole).Distinct().ToList();

        roles.Should().Contain(ChoRole.Client, "CHO drives an external server in the burden-reduction scenarios");
        roles.Should().Contain(ChoRole.Server, "an Inferno suite drives CHO as the system under test");
    }

    [Fact]
    public void Scenarios_that_need_cho_running_say_so_in_their_required_services()
    {
        foreach (var scenario in Inventory.Scenarios.Where(s => s.ParsedChoRole == ChoRole.Server))
        {
            scenario.RequiredServices.Should().Contain("cho-fhir-service",
                "'{0}' has CHO answering the requests, so the environment must start CHO", scenario.Id);
        }
    }

    [Fact]
    public void The_default_developer_path_starts_only_what_the_scenarios_need()
    {
        // The documented commands must not start every external tool. Both
        // br-payer scenarios need exactly one external service.
        Inventory.Scenario("BR-PAS-SUBMIT-001").RequiredServices.Should().BeEquivalentTo(["br-payer"]);
        Inventory.Scenario("BR-CRD-001").RequiredServices.Should().BeEquivalentTo(["br-payer"]);
    }

    [Fact]
    public void The_crd_scenario_is_declared_as_cho_driving_an_external_payer()
    {
        var crd = Inventory.Scenario("BR-CRD-001");

        crd.Protocol.Should().Be("CRD");
        crd.ParsedChoRole.Should().Be(ChoRole.Client,
            "CHO is the provider-side CRD client; the external implementation is the payer CDS service");
        crd.ExternalTarget.Should().Be("br-payer");
    }
}
