using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// Verifies that every canonical URL appearing in a shipped artifact
/// (StructureDefinition, CodeSystem, ValueSet, OperationDefinition) is
/// represented by a constant in <see cref="ChoFhirCanonicalUrls"/>.
/// Protects against copy-paste typos that would be load-bearing and
/// permanent once a resource claims conformance via <c>meta.profile</c>
/// or references a CodeSystem/ValueSet URL.
/// </summary>
public class CanonicalUrlConstantsTests
{
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    [Fact]
    public void Every_StructureDefinition_url_has_matching_constant()
    {
        var urlsFromArtifacts = TestArtifactFiles
            .JsonFilesMatching("StructureDefinition-")
            .Select(path => _parser.Parse<StructureDefinition>(TestArtifactFiles.ReadAllText(path)).Url)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        var urlsFromConstants = ChoFhirCanonicalUrls.AllStructureDefinitions
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        urlsFromArtifacts.Should().BeEquivalentTo(urlsFromConstants,
            "ChoFhirCanonicalUrls must list exactly the StructureDefinition URLs shipped");
    }

    [Fact]
    public void Every_CodeSystem_url_has_matching_constant()
    {
        var urlsFromArtifacts = TestArtifactFiles
            .JsonFilesMatching("CodeSystem-")
            .Select(path => _parser.Parse<CodeSystem>(TestArtifactFiles.ReadAllText(path)).Url)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        urlsFromArtifacts.Should().BeEquivalentTo(ChoFhirCanonicalUrls.AllCodeSystems);
    }

    [Fact]
    public void Every_ValueSet_url_has_matching_constant()
    {
        var urlsFromArtifacts = TestArtifactFiles
            .JsonFilesMatching("ValueSet-")
            .Select(path => _parser.Parse<ValueSet>(TestArtifactFiles.ReadAllText(path)).Url)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        urlsFromArtifacts.Should().BeEquivalentTo(ChoFhirCanonicalUrls.AllValueSets);
    }

    [Fact]
    public void Every_OperationDefinition_url_has_matching_constant()
    {
        var urlsFromArtifacts = TestArtifactFiles
            .JsonFilesMatching("OperationDefinition-")
            .Select(path => _parser.Parse<OperationDefinition>(TestArtifactFiles.ReadAllText(path)).Url)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

        urlsFromArtifacts.Should().BeEquivalentTo(ChoFhirCanonicalUrls.AllOperationDefinitions);
    }

    [Fact]
    public void Every_binding_valueSet_reference_points_at_an_existing_ValueSet_artifact()
    {
        // CHO profile bindings reference ValueSet URLs via `binding.valueSet`.
        // Every such reference that points into the CHO canonical namespace
        // must resolve to an artifact we actually ship.
        var shippedValueSetUrls = ChoFhirCanonicalUrls.AllValueSets.ToHashSet(StringComparer.Ordinal);

        foreach (var path in TestArtifactFiles.JsonFilesMatching("StructureDefinition-"))
        {
            var sd = _parser.Parse<StructureDefinition>(TestArtifactFiles.ReadAllText(path));
            foreach (var elem in sd.Differential.Element)
            {
                var vsRef = elem.Binding?.ValueSet?.ToString();
                if (vsRef is not null &&
                    vsRef.StartsWith(ChoFhirCanonicalUrls.ValueSetBase, StringComparison.Ordinal))
                {
                    shippedValueSetUrls.Should().Contain(vsRef,
                        $"{Path.GetFileName(path)} element {elem.Path} binds to {vsRef}, " +
                        "which must exist as a shipped ValueSet artifact");
                }
            }
        }
    }

    [Fact]
    public void Extension_profile_references_point_at_existing_extension_artifacts()
    {
        var existingExtensionUrls = TestArtifactFiles
            .JsonFilesMatching("StructureDefinition-")
            .Select(path => _parser.Parse<StructureDefinition>(TestArtifactFiles.ReadAllText(path)))
            .Where(sd => sd.Type == "Extension")
            .Select(sd => sd.Url)
            .ToHashSet(StringComparer.Ordinal);

        var profilePaths = TestArtifactFiles.JsonFilesMatching("StructureDefinition-cho-appeal-")
            .Where(p => !Path.GetFileName(p).Contains("extension", StringComparison.OrdinalIgnoreCase));

        foreach (var path in profilePaths)
        {
            var sd = _parser.Parse<StructureDefinition>(TestArtifactFiles.ReadAllText(path));
            if (sd.Type == "Extension") continue;

            foreach (var elem in sd.Differential.Element)
            {
                foreach (var typeRef in elem.Type ?? [])
                {
                    if (typeRef.Code != "Extension") continue;
                    foreach (var refUrl in typeRef.Profile ?? [])
                    {
                        if (refUrl.StartsWith(ChoFhirCanonicalUrls.StructureDefinitionBase,
                                StringComparison.Ordinal))
                        {
                            existingExtensionUrls.Should().Contain(refUrl,
                                $"{Path.GetFileName(path)} references extension {refUrl} that must exist as an artifact");
                        }
                    }
                }
            }
        }
    }
}
