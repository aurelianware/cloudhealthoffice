using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace CloudHealthOffice.FhirService.Tests.FhirArtifacts;

/// <summary>
/// Asserts each CHO CodeSystem enumerates the exact concept codes required
/// by the original specification. Catches accidental additions/removals.
/// </summary>
public class CodeSystemConceptTests
{
    private static readonly FhirJsonParser _parser = new(new ParserSettings { PermissiveParsing = false });

    public static IEnumerable<object[]> CodeSystems()
    {
        return new[]
        {
            new object[]
            {
                "CodeSystem-cho-appeal-type.json",
                new[] { "reconsideration", "peer-review", "external-review", "grievance" }
            },
            new object[]
            {
                "CodeSystem-cho-appeal-level.json",
                new[] { "first-level", "second-level", "external-review" }
            },
            new object[]
            {
                "CodeSystem-cho-appeal-line-of-business.json",
                new[] { "commercial", "medicare", "medicaid", "marketplace" }
            },
            new object[]
            {
                "CodeSystem-cho-appeal-x12-275-transmission-code.json",
                new[] { "AA", "BM", "EL", "FT", "FX", "IL", "OZ" }
            },
            new object[]
            {
                "CodeSystem-cho-appeal-communication-category.json",
                new[] { "appeal-argument", "reviewer-note", "decision-rationale" }
            },
            new object[]
            {
                "CodeSystem-cho-appeal-attachment-type.json",
                new[]
                {
                    "provider-appeal-letter", "medical-records", "operative-report",
                    "progress-note", "lab-results", "imaging-report", "other"
                }
            },
        };
    }

    [Theory]
    [MemberData(nameof(CodeSystems))]
    public void CodeSystem_concepts_match_expected_codes(string fileName, string[] expectedCodes)
    {
        var absPath = Path.Combine(TestArtifactFiles.ProfilesDirectory, fileName);
        var cs = _parser.Parse<CodeSystem>(TestArtifactFiles.ReadAllText(absPath));

        cs.Content?.ToString().Should().Be("Complete");
        cs.CaseSensitive.Should().BeTrue();
        cs.Concept.Select(c => c.Code).Should().BeEquivalentTo(expectedCodes);
    }

    [Theory]
    [InlineData("ValueSet-cho-appeal-task-status.json",
        "http://hl7.org/fhir/task-status",
        new[] { "requested", "accepted", "in-progress", "on-hold", "completed", "rejected", "cancelled" })]
    [InlineData("ValueSet-cho-appeal-communication-status.json",
        "http://hl7.org/fhir/event-status",
        new[] { "in-progress", "completed" })]
    [InlineData("ValueSet-cho-appeal-document-status.json",
        "http://hl7.org/fhir/document-reference-status",
        new[] { "current", "superseded", "entered-in-error" })]
    public void HL7_narrowing_ValueSets_use_explicit_concept_enumeration(
        string fileName, string expectedSystem, string[] expectedCodes)
    {
        var absPath = Path.Combine(TestArtifactFiles.ProfilesDirectory, fileName);
        var vs = _parser.Parse<ValueSet>(TestArtifactFiles.ReadAllText(absPath));

        vs.Compose.Should().NotBeNull();
        vs.Compose.Include.Should().HaveCount(1);

        var include = vs.Compose.Include.Single();
        include.System.Should().Be(expectedSystem);
        include.Filter.Should().BeNullOrEmpty(
            "HL7-narrowing ValueSets must not use filter[] — " +
            "codes must be enumerated explicitly so offline validators " +
            "do not require a live terminology server");
        include.Concept.Select(c => c.Code).Should().BeEquivalentTo(expectedCodes);
    }
}
