using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// For each CHO extension, asserts that <c>context[0]</c> correctly scopes
/// the extension to the intended base resource element.
/// </summary>
public class ExtensionContextTests
{
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public static IEnumerable<object[]> Extensions()
    {
        return new[]
        {
            new object[] { "StructureDefinition-cho-appeal-level.json",                      "Task" },
            new object[] { "StructureDefinition-cho-appeal-line-of-business.json",           "Task" },
            new object[] { "StructureDefinition-cho-appeal-target-response-date.json",       "Task" },
            new object[] { "StructureDefinition-cho-appeal-urgent-flag.json",                "Task" },
            new object[] { "StructureDefinition-cho-appeal-x12-275-control-number.json",     "DocumentReference.identifier" },
            new object[] { "StructureDefinition-cho-appeal-x12-275-transmission-code.json",  "DocumentReference.content.format" },
            new object[] { "StructureDefinition-cho-appeal-task-reference.json",             "ClaimResponse" },
        };
    }

    [Theory]
    [MemberData(nameof(Extensions))]
    public void Extension_context_scopes_to_expected_base_element(string fileName, string expectedContext)
    {
        var absPath = Path.Combine(TestArtifactFiles.ProfilesDirectory, fileName);
        var sd = _parser.Parse<StructureDefinition>(TestArtifactFiles.ReadAllText(absPath));

        sd.Type.Should().Be("Extension");
        sd.Kind.Should().Be(StructureDefinition.StructureDefinitionKind.ComplexType);
        sd.BaseDefinition.Should().Be("http://hl7.org/fhir/StructureDefinition/Extension");
        sd.Derivation.Should().Be(StructureDefinition.TypeDerivationRule.Constraint);

        sd.Context.Should().HaveCount(1);
        sd.Context[0].Type.Should().Be(StructureDefinition.ExtensionContextType.Element);
        sd.Context[0].Expression.Should().Be(expectedContext);

        // Extension.url fixed-uri must match the StructureDefinition.url.
        var urlElement = sd.Differential.Element.Single(e => e.Path == "Extension.url");
        urlElement.Fixed.Should().BeOfType<FhirUri>();
        ((FhirUri)urlElement.Fixed).Value.Should().Be(sd.Url);
    }
}
