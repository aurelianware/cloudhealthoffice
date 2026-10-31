using FluentAssertions;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// A run against a deployed CHO hands the kit the plan's own address, patients
/// and token. It must refuse what it cannot run honestly, and never let the
/// token reach the evidence.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ChoRemoteTargetTests
{
    private static readonly InteropScenarioInventory Inventory = InteropScenarioInventory.Load();

    private const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ0ZXN0In0.c2lnbmF0dXJlLXZhbHVl";

    private static ChoRemoteTarget Target(Dictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            [ChoRemoteTarget.UrlVariable] = "https://api.plan.example/fhir/r4/",
            [ChoRemoteTarget.DirectoryUrlVariable] = "https://api.plan.example/fhir/r4/directory/plan",
            [ChoRemoteTarget.PatientIdsVariable] = " M-1001, M-1002 ,M-1001",
            [ChoRemoteTarget.TokenVariable] = Token,
            [ChoRemoteTarget.LabelVariable] = "Example Health Plan",
        };
        foreach (var (key, value) in overrides ?? [])
            values[key] = value;
        return ChoRemoteTarget.FromValues(name => values.GetValueOrDefault(name))!;
    }

    [Fact]
    public void Remote_mode_is_off_unless_a_target_url_is_set()
    {
        ChoRemoteTarget.FromValues(_ => null).Should().BeNull();
        ChoRemoteTarget.FromValues(name => name == ChoRemoteTarget.UrlVariable ? "  " : "x").Should().BeNull();
    }

    [Fact]
    public void Reads_the_target_and_normalises_patient_ids()
    {
        var target = Target();

        target.Label.Should().Be("Example Health Plan");
        target.PatientIds.Should().Equal("M-1001", "M-1002");
        target.BaseUrl(ChoSurfaces.Fhir).Should().Be("https://api.plan.example/fhir/r4");
        target.BaseUrl(ChoSurfaces.PublicDirectory).Should().Be("https://api.plan.example/fhir/r4/directory/plan");
    }

    [Fact]
    public void The_label_defaults_to_the_host()
        => Target(new() { [ChoRemoteTarget.LabelVariable] = null }).Label.Should().Be("api.plan.example");

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://api.plan.example/fhir")]
    [InlineData("http://localhost:8080/fhir/r4")]
    [InlineData("http://127.0.0.1:18023/fhir/r4")]
    public void Refuses_an_address_the_kit_cannot_reach(string url)
    {
        var act = () => Target(new() { [ChoRemoteTarget.UrlVariable] = url });
        act.Should().Throw<InteropEnvironmentException>().WithMessage($"*{ChoRemoteTarget.UrlVariable}*");
    }

    [Fact]
    public void Carin_bb_is_given_the_plans_patients_and_token_not_the_seed()
    {
        var scenario = Inventory.Scenario("INFERNO-C4BB-SERVER-001");
        var inputs = Target().BuildInputs(scenario).ToDictionary(i => i.Name);

        inputs.Keys.Should().BeEquivalentTo(scenario.InfernoInputs);
        inputs["url"].Value.Should().Be("https://api.plan.example/fhir/r4");
        inputs["patient_ids"].Value.Should().Be("M-1001,M-1002");
        inputs["patient_ids"].Value.Should().NotContain(ChoSeedFixture.Load().Member.MemberId);

        var auth = System.Text.Json.JsonDocument.Parse(inputs["smart_auth_info"].Value).RootElement;
        auth.GetProperty("access_token").GetString().Should().Be(Token);
    }

    [Fact]
    public void Us_core_runs_with_the_same_inputs()
    {
        var scenario = Inventory.Scenario("INFERNO-USCORE-SERVER-001");
        Target().BuildInputs(scenario).Select(i => i.Name).Should().BeEquivalentTo(scenario.InfernoInputs);
    }

    [Fact]
    public void Plan_net_is_given_the_public_directory_and_needs_no_token()
    {
        var scenario = Inventory.Scenario("INFERNO-PLANNET-SERVER-001");
        var target = Target(new()
        {
            [ChoRemoteTarget.TokenVariable] = null,
            [ChoRemoteTarget.PatientIdsVariable] = null,
        });

        target.BuildInputs(scenario).Single(i => i.Name == "url").Value
            .Should().Be("https://api.plan.example/fhir/r4/directory/plan");
    }

    [Fact]
    public void Plan_net_without_a_directory_url_is_refused_by_name()
    {
        var scenario = Inventory.Scenario("INFERNO-PLANNET-SERVER-001");
        var act = () => Target(new() { [ChoRemoteTarget.DirectoryUrlVariable] = null }).EnsureCanRun(scenario);
        act.Should().Throw<InteropEnvironmentException>().WithMessage($"*{ChoRemoteTarget.DirectoryUrlVariable}*");
    }

    [Theory]
    [InlineData(ChoRemoteTarget.PatientIdsVariable)]
    [InlineData(ChoRemoteTarget.TokenVariable)]
    public void Patient_access_without_patients_or_a_token_is_refused_by_name(string missing)
    {
        var scenario = Inventory.Scenario("INFERNO-C4BB-SERVER-001");
        var act = () => Target(new() { [missing] = null }).EnsureCanRun(scenario);
        act.Should().Throw<InteropEnvironmentException>().WithMessage($"*{missing}*");
    }

    [Theory]
    [InlineData("INFERNO-BULK-SERVER-001")]
    [InlineData("INFERNO-PDEX-SERVER-001")]
    public void Self_authenticating_kits_are_refused_rather_than_run_without_registration(string scenarioId)
    {
        var act = () => Target().EnsureCanRun(Inventory.Scenario(scenarioId));
        act.Should().Throw<InteropEnvironmentException>().WithMessage("*SMART Backend Services*");
    }

    [Fact]
    public void The_description_never_carries_the_token_or_patient_ids()
    {
        var described = Target().Describe();

        described.Should().NotContain(Token).And.NotContain("M-1001");
        described.Should().Contain("Example Health Plan").And.Contain("2 patient id(s)");
        Target().ToString().Should().Be(described);
    }
}
