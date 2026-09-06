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
    public void Only_the_scenario_this_harness_actually_executes_is_marked_implemented()
    {
        Inventory.Scenarios
            .Where(s => s.Implemented)
            .Select(s => s.Id)
            .Should().BeEquivalentTo(["BR-PAS-SUBMIT-001"],
                "an inventory row is a plan, not a result; marking an unexecuted scenario implemented would " +
                "produce a green row nothing proved");
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
    public void The_default_developer_path_starts_only_what_the_smoke_scenario_needs()
    {
        Inventory.Scenario("BR-PAS-SUBMIT-001").RequiredServices
            .Should().BeEquivalentTo(["br-payer"],
                "the documented smoke command must not start every external tool");
    }
}
