using System.IO;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// Verifies that every CHO-authored FHIR artifact under docs/fhir/profiles/
/// parses as a valid FHIR R4 resource via the Firely parser, and that
/// core metadata fields (url, status, fhirVersion, experimental) are set
/// as expected.
/// </summary>
public class ArtifactValidityTests
{
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public static IEnumerable<object[]> AllArtifactFiles
        => TestArtifactFiles.AllJsonFiles.Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(AllArtifactFiles))]
    public void Artifact_parses_and_has_expected_metadata(string absolutePath)
    {
        var json = TestArtifactFiles.ReadAllText(absolutePath);

        var resource = _parser.Parse<Resource>(json);

        resource.Should().NotBeNull("artifact must parse as a FHIR resource");

        // Every CHO artifact declares url / status / version fields via the
        // `IVersionableConformanceResource`-style properties. Project onto
        // the common `CanonicalResource`-style surface.
        string? url = null;
        PublicationStatus? status = null;
        string? fhirVersionStr = null;
        bool experimental = false;

        switch (resource)
        {
            case StructureDefinition sd:
                url = sd.Url; status = sd.Status;
                fhirVersionStr = sd.FhirVersionElement?.ObjectValue?.ToString();
                experimental = sd.Experimental ?? false;
                break;
            case CodeSystem cs:
                url = cs.Url; status = cs.Status;
                experimental = cs.Experimental ?? false;
                break;
            case ValueSet vs:
                url = vs.Url; status = vs.Status;
                experimental = vs.Experimental ?? false;
                break;
            case OperationDefinition od:
                url = od.Url; status = od.Status;
                experimental = od.Experimental ?? false;
                break;
            default:
                throw new InvalidOperationException(
                    $"Unexpected resource type in artifact: {resource.TypeName}");
        }

        url.Should().StartWith("http://fhir.cloudhealthoffice.com/",
            "all CHO artifacts live under the CHO canonical namespace");
        status.Should().Be(PublicationStatus.Active);
        experimental.Should().BeFalse("CHO ships these as production conformance artifacts");

        // StructureDefinition is the only resource type where fhirVersion is
        // on the root; for CodeSystem/ValueSet/OperationDefinition the R4
        // version is implicit in the parse pipeline.
        if (resource is StructureDefinition)
        {
            fhirVersionStr.Should().Be("4.0.1");
        }

        // Filename must match HL7 convention `{resourceType}-{id}.json`.
        var fileName = Path.GetFileNameWithoutExtension(absolutePath);
        fileName.Should().StartWith($"{resource.TypeName}-");
        var expectedIdPart = fileName.Substring(resource.TypeName!.Length + 1);
        resource.Id.Should().Be(expectedIdPart,
            "filename id part must match resource.id");
    }

    [Fact]
    public void Artifact_count_matches_expected_totals()
    {
        var all = TestArtifactFiles.AllJsonFiles.ToList();
        all.Should().HaveCount(27, "PR 1 ships exactly 27 JSON artifacts");

        all.Count(p => Path.GetFileName(p).StartsWith("StructureDefinition-"))
            .Should().Be(11, "4 profiles + 7 extensions = 11 StructureDefinitions");
        all.Count(p => Path.GetFileName(p).StartsWith("CodeSystem-"))
            .Should().Be(6);
        all.Count(p => Path.GetFileName(p).StartsWith("ValueSet-"))
            .Should().Be(9);
        all.Count(p => Path.GetFileName(p).StartsWith("OperationDefinition-"))
            .Should().Be(1);
    }
}
