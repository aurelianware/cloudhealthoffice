using FluentAssertions;
using Hl7.Fhir.Model;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// Package indexing decides whether "the payer sent a complete package" is a
/// claim the evidence can make. Resolving a dependency that isn't there, or
/// quietly matching a different version than was asked for, would turn a real
/// incompatibility into a green row.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class PackageResourceIndexTests
{
    private static Bundle PackageOf(params Resource[] resources) => new()
    {
        Type = Bundle.BundleType.Collection,
        Entry = resources.Select(resource => new Bundle.EntryComponent { Resource = resource }).ToList(),
    };

    private static Questionnaire NewQuestionnaire(string url, string? version = null) =>
        new() { Url = url, Version = version, Status = PublicationStatus.Active };

    private static Library NewLibrary(string url, string? version = null) =>
        new() { Url = url, Version = version, Status = PublicationStatus.Active };

    [Fact]
    public void The_index_reports_the_package_inventory()
    {
        var index = new PackageResourceIndex(PackageOf(
            NewQuestionnaire("http://example.org/Q1"),
            NewLibrary("http://example.org/L1"),
            new ValueSet { Url = "http://example.org/VS1", Status = PublicationStatus.Active },
            new QuestionnaireResponse { Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress }));

        index.ResourceTypeCounts.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Questionnaire"] = 1, ["Library"] = 1, ["ValueSet"] = 1, ["QuestionnaireResponse"] = 1,
        });
        index.Canonicals.Should().Equal(
            "http://example.org/L1", "http://example.org/Q1", "http://example.org/VS1");
    }

    [Fact]
    public void A_versionless_reference_resolves_to_any_version_present()
    {
        var index = new PackageResourceIndex(PackageOf(NewQuestionnaire("http://example.org/Q1", "1.0.0")));

        index.Resolve("http://example.org/Q1").Should().NotBeNull();
    }

    [Fact]
    public void A_versioned_reference_resolves_only_to_that_version()
    {
        var index = new PackageResourceIndex(PackageOf(NewQuestionnaire("http://example.org/Q1", "1.0.0")));

        index.Resolve("http://example.org/Q1|1.0.0").Should().NotBeNull();
        index.Resolve("http://example.org/Q1|2.0.0").Should().BeNull(
            "a different version is a different artifact, not a near-enough match");
    }

    [Fact]
    public void A_version_that_is_present_at_another_version_is_reported_as_a_mismatch_not_as_missing()
    {
        var index = new PackageResourceIndex(PackageOf(NewLibrary("http://example.org/L1", "1.0.0")));
        var references = new[] { "http://example.org/L1|2.0.0" };

        // The consequence differs: a consumer gets a resource, just not the one it
        // asked for. Collapsing the two would hide that.
        index.UnresolvedReferences(references).Should().BeEmpty();
        index.VersionMismatches(references).Should().ContainSingle()
            .Which.Should().Contain("1.0.0");
    }

    [Fact]
    public void A_reference_the_package_does_not_contain_is_unresolved()
    {
        var index = new PackageResourceIndex(PackageOf(NewQuestionnaire("http://example.org/Q1")));

        index.UnresolvedReferences(["http://example.org/L-missing"])
            .Should().ContainSingle().Which.Should().Be("http://example.org/L-missing");
    }

    [Fact]
    public void A_canonical_defined_twice_is_reported_rather_than_silently_first_wins()
    {
        var index = new PackageResourceIndex(PackageOf(
            NewQuestionnaire("http://example.org/Q1", "1.0.0"),
            NewQuestionnaire("http://example.org/Q1", "2.0.0")));

        index.DuplicateCanonicals.Should().ContainSingle().Which.Should().Be("http://example.org/Q1");
    }

    [Fact]
    public void An_empty_or_absent_bundle_indexes_to_nothing_without_throwing()
    {
        new PackageResourceIndex(null).Resources.Should().BeEmpty();
        new PackageResourceIndex(PackageOf()).Canonicals.Should().BeEmpty();
    }

    [Fact]
    public void Questionnaire_dependencies_cover_library_valueset_and_subquestionnaire()
    {
        var questionnaire = NewQuestionnaire("http://example.org/Q1");
        questionnaire.Extension.Add(new Extension(
            PackageResourceIndex.CqfLibraryExtension, new Canonical("http://example.org/L1")));

        var child = new Questionnaire.ItemComponent
        {
            LinkId = "child", Type = Questionnaire.QuestionnaireItemType.Choice,
            AnswerValueSet = "http://example.org/VS1",
        };
        child.Extension.Add(new Extension(
            PackageResourceIndex.SubQuestionnaireExtension, new Canonical("http://example.org/Q2")));

        questionnaire.Item.Add(new Questionnaire.ItemComponent
        {
            LinkId = "parent", Type = Questionnaire.QuestionnaireItemType.Group,
            Item = { child },
        });

        // Nested: a dependency three levels down is no less required.
        PackageResourceIndex.QuestionnaireDependencies(questionnaire).Should().BeEquivalentTo(
            ["http://example.org/L1", "http://example.org/VS1", "http://example.org/Q2"]);
    }

    [Fact]
    public void A_questionnaire_declaring_no_dependencies_has_none()
    {
        PackageResourceIndex.QuestionnaireDependencies(NewQuestionnaire("http://example.org/Q1"))
            .Should().BeEmpty("a questionnaire that names nothing is complete on its own");
        PackageResourceIndex.QuestionnaireDependencies(null).Should().BeEmpty();
    }

    [Fact]
    public void Duplicate_dependency_references_are_reported_once()
    {
        var questionnaire = NewQuestionnaire("http://example.org/Q1");
        questionnaire.Item.Add(new Questionnaire.ItemComponent
        {
            LinkId = "a", Type = Questionnaire.QuestionnaireItemType.Choice,
            AnswerValueSet = "http://example.org/VS1",
        });
        questionnaire.Item.Add(new Questionnaire.ItemComponent
        {
            LinkId = "b", Type = Questionnaire.QuestionnaireItemType.Choice,
            AnswerValueSet = "http://example.org/VS1",
        });

        PackageResourceIndex.QuestionnaireDependencies(questionnaire).Should().ContainSingle();
    }
}
