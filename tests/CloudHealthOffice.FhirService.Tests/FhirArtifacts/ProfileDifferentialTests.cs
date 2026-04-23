using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// For each CHO resource profile, walks <c>differential.element[]</c> and
/// asserts every element <c>path</c> starts with <c>{type}.</c> — i.e.
/// the differential only references paths on the intended base resource.
/// </summary>
public class ProfileDifferentialTests
{
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public static IEnumerable<object[]> ResourceProfiles()
    {
        return new[]
        {
            new object[] { "StructureDefinition-cho-appeal-task.json",                "Task" },
            new object[] { "StructureDefinition-cho-appeal-communication.json",       "Communication" },
            new object[] { "StructureDefinition-cho-appeal-document-reference.json",  "DocumentReference" },
            new object[] { "StructureDefinition-cho-appeal-claim-response.json",      "ClaimResponse" },
        };
    }

    [Theory]
    [MemberData(nameof(ResourceProfiles))]
    public void Resource_profile_differential_paths_belong_to_base_type(string fileName, string expectedType)
    {
        var absPath = Path.Combine(TestArtifactFiles.ProfilesDirectory, fileName);
        var sd = _parser.Parse<StructureDefinition>(TestArtifactFiles.ReadAllText(absPath));

        sd.Type.Should().Be(expectedType);
        sd.Kind.Should().Be(StructureDefinition.StructureDefinitionKind.Resource);
        sd.BaseDefinition.Should().Be($"http://hl7.org/fhir/StructureDefinition/{expectedType}");
        sd.Derivation.Should().Be(StructureDefinition.TypeDerivationRule.Constraint);
        sd.Abstract.Should().BeFalse();

        sd.Differential.Should().NotBeNull();
        sd.Differential.Element.Should().NotBeEmpty();

        foreach (var elem in sd.Differential.Element)
        {
            elem.Path.Should().NotBeNullOrEmpty();
            (elem.Path == expectedType || elem.Path.StartsWith($"{expectedType}."))
                .Should().BeTrue(
                    $"element path '{elem.Path}' in {fileName} must be rooted at '{expectedType}'");
        }

        // Profile description should reference the conformance-claim sentence.
        sd.Description.ToString().Should().Contain(
            "does not claim conformance to any external Implementation Guide",
            "every CHO profile must state its conformance posture");
    }
}
