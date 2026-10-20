using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudHealthOffice.PlanEdits.Contracts.Tests;

/// <summary>
/// The JSON Schemas published for plans on other stacks
/// (docs/contracts/plan-edits/v1) state the same limits and patterns the .NET
/// contract enforces, and name every property the .NET types carry.
/// </summary>
public class PublishedSchemaTests
{
    private static JsonNode Schema(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "contracts", "plan-edits")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test runs inside the repository");
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "docs", "contracts", "plan-edits", "v1", name)))!;
    }

    [Fact]
    public void The_response_schema_matches_the_validator()
    {
        var schema = Schema("response.schema.json");
        var edit = schema["$defs"]!["edit"]!["properties"]!;

        schema["properties"]!["contractVersion"]!["const"]!.GetValue<string>().Should().Be(PlanEditContract.Version);
        schema["properties"]!["edits"]!["maxItems"]!.GetValue<int>().Should().Be(PlanEditContract.MaxEdits);
        edit["ruleId"]!["maxLength"]!.GetValue<int>().Should().Be(PlanEditContract.MaxRuleIdLength);
        edit["ruleId"]!["pattern"]!.GetValue<string>().Should().Be(PlanEditResponseValidator.RuleIdPattern);
        edit["message"]!["maxLength"]!.GetValue<int>().Should().Be(PlanEditContract.MaxMessageLength);
        edit["suggestedCarc"]!["pattern"]!.GetValue<string>().Should().Be(PlanEditResponseValidator.CarcPattern);
        edit["suggestedRarc"]!["pattern"]!.GetValue<string>().Should().Be(PlanEditResponseValidator.RarcPattern);
        schema["description"]!.GetValue<string>().Should().Contain(PlanEditContract.MaxResponseBytes.ToString());

        PropertyNames(typeof(PlanEdit)).Should().BeEquivalentTo(((JsonObject)edit).Select(p => p.Key));
    }

    [Fact]
    public void The_request_schema_names_every_property_the_request_types_carry()
    {
        var schema = Schema("request.schema.json");
        var claim = (JsonObject)schema["$defs"]!["claim"]!["properties"]!;

        PropertyNames(typeof(PlanEditRequest)).Should().BeEquivalentTo(((JsonObject)schema["properties"]!).Select(p => p.Key));
        PropertyNames(typeof(PlanEditClaim)).Should().BeEquivalentTo(claim.Select(p => p.Key));
        PropertyNames(typeof(PlanEditMember)).Should().BeEquivalentTo(((JsonObject)claim["member"]!["properties"]!).Select(p => p.Key));
        PropertyNames(typeof(PlanEditDiagnosis)).Should().BeEquivalentTo(((JsonObject)claim["diagnoses"]!["items"]!["properties"]!).Select(p => p.Key));
        PropertyNames(typeof(PlanEditLine)).Should().BeEquivalentTo(((JsonObject)claim["lines"]!["items"]!["properties"]!).Select(p => p.Key));
    }

    private static IEnumerable<string> PropertyNames(Type type) =>
        type.GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));
}
