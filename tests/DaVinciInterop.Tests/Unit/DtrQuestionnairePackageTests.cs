using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// The DTR package view decides whether "the payer returned a usable package" is
/// something the evidence can claim. It must report what the server sent — never
/// demand resource types a conformant server had no reason to include, and never
/// treat a missing package as an empty one.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class DtrQuestionnairePackageTests
{
    private const string Canonical = "http://example.org/fhir/Questionnaire/PriorAuthRequired";

    private static Parameters PackageResponse(Bundle? bundle, OperationOutcome? outcome = null)
    {
        var parameters = new Parameters();
        if (bundle is not null)
        {
            parameters.Parameter.Add(new Parameters.ParameterComponent
            {
                Name = DtrQuestionnairePackage.PackageBundleParameter,
                Resource = bundle,
            });
        }

        if (outcome is not null)
        {
            parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "outcome", Resource = outcome });
        }

        return parameters;
    }

    private static Bundle BundleWith(params Resource[] resources) => new()
    {
        Type = Bundle.BundleType.Collection,
        Entry = resources.Select(r => new Bundle.EntryComponent { Resource = r }).ToList(),
    };

    private static Questionnaire StandardQuestionnaire(string url = Canonical) => new()
    {
        Id = "PriorAuthRequired",
        Url = url,
        Status = PublicationStatus.Active,
        Meta = new Meta { Profile = [DtrQuestionnairePackage.StandardQuestionnaireProfile] },
    };

    [Fact]
    public void A_well_formed_package_parses_and_reports_no_violations()
    {
        var package = DtrQuestionnairePackage.From(PackageResponse(BundleWith(
            StandardQuestionnaire(),
            new QuestionnaireResponse
            {
                Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
                Questionnaire = Canonical,
            })))!;

        package.ProtocolViolations().Should().BeEmpty();
        package.Questionnaires.Should().ContainSingle();
        package.QuestionnaireResponses.Should().ContainSingle();
        package.Questionnaire(Canonical).Should().NotBeNull();
    }

    [Fact]
    public void A_package_carrying_only_a_questionnaire_is_complete_when_it_declares_no_dependencies()
    {
        var package = DtrQuestionnairePackage.From(PackageResponse(BundleWith(StandardQuestionnaire())))!;

        // Demanding a Library or ValueSet would fail a conformant server whose
        // questionnaire simply names none.
        package.ProtocolViolations().Should().BeEmpty();
    }

    [Fact]
    public void A_missing_package_bundle_is_a_violation_that_names_what_did_arrive()
    {
        var package = DtrQuestionnairePackage.From(PackageResponse(
            bundle: null, outcome: new OperationOutcome()))!;

        package.PackageBundle.Should().BeNull("no package is not the same as an empty package");
        package.ProtocolViolations().Should().ContainSingle()
            .Which.Should().Contain("packagebundle").And.Contain("outcome");
    }

    [Fact]
    public void A_package_with_no_questionnaire_is_a_violation()
    {
        var package = DtrQuestionnairePackage.From(PackageResponse(BundleWith(
            new QuestionnaireResponse { Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress })))!;

        package.ProtocolViolations().Should().Contain(v => v.Contains("no Questionnaire"));
    }

    [Fact]
    public void A_questionnaire_without_a_canonical_url_is_a_violation()
    {
        var questionnaire = StandardQuestionnaire();
        questionnaire.Url = null;

        DtrQuestionnairePackage.From(PackageResponse(BundleWith(questionnaire)))!
            .ProtocolViolations().Should().Contain(v => v.Contains("no canonical url"));
    }

    [Fact]
    public void A_dependency_the_package_omits_is_reported_as_a_violation()
    {
        var questionnaire = StandardQuestionnaire();
        questionnaire.Extension.Add(new Extension(
            PackageResourceIndex.CqfLibraryExtension, new Canonical("http://example.org/Library/Missing")));

        DtrQuestionnairePackage.From(PackageResponse(BundleWith(questionnaire)))!
            .ProtocolViolations().Should().ContainSingle()
            .Which.Should().Contain("http://example.org/Library/Missing")
            .And.Contain("does not contain");
    }

    [Fact]
    public void A_dependency_the_package_includes_is_not_a_violation()
    {
        var questionnaire = StandardQuestionnaire();
        questionnaire.Extension.Add(new Extension(
            PackageResourceIndex.CqfLibraryExtension, new Canonical("http://example.org/Library/Rule")));

        var package = DtrQuestionnairePackage.From(PackageResponse(BundleWith(
            questionnaire,
            new Library { Url = "http://example.org/Library/Rule", Status = PublicationStatus.Active })))!;

        package.ProtocolViolations().Should().BeEmpty();
    }

    [Fact]
    public void A_non_parameters_response_is_not_a_package()
    {
        DtrQuestionnairePackage.From(new OperationOutcome()).Should().BeNull();
        DtrQuestionnairePackage.From(null).Should().BeNull();
    }

    [Fact]
    public void An_operation_outcome_is_surfaced_wherever_the_server_attached_it()
    {
        var outcome = new OperationOutcome
        {
            Issue =
            {
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Warning,
                    Code = OperationOutcome.IssueType.Informational,
                    Details = new CodeableConcept { Text = "pre-population skipped" },
                },
            },
        };

        var package = DtrQuestionnairePackage.From(
            PackageResponse(BundleWith(StandardQuestionnaire()), outcome))!;

        package.Outcomes.Should().ContainSingle();
        ParametersExtractor.SummarizeIssues(package.Outcomes[0])
            .Should().ContainSingle().Which.Should().Contain("pre-population skipped");
        // A warning the server chose to send is not a protocol violation.
        package.ProtocolViolations().Should().BeEmpty();
    }

    [Fact]
    public void An_adaptive_questionnaire_is_identified_by_its_profile()
    {
        var adaptive = StandardQuestionnaire();
        adaptive.Meta = new Meta { Profile = [DtrQuestionnairePackage.AdaptiveQuestionnaireProfile] };

        DtrQuestionnairePackage.IsAdaptive(adaptive).Should().BeTrue();
        DtrQuestionnairePackage.IsAdaptive(StandardQuestionnaire()).Should().BeFalse(
            "a standard questionnaire is usable straight from the package");
    }

    [Fact]
    public void The_safe_summary_is_an_inventory_and_carries_no_questionnaire_content()
    {
        var questionnaire = StandardQuestionnaire();
        questionnaire.Item.Add(new Questionnaire.ItemComponent
        {
            LinkId = "q1",
            Type = Questionnaire.QuestionnaireItemType.String,
            Text = "Patient clinical narrative that must never reach evidence",
        });

        var summary = DtrQuestionnairePackage.From(PackageResponse(BundleWith(questionnaire)))!.SafeSummary();

        summary.Should().Contain("Questionnaire=1").And.Contain(Canonical);
        summary.Should().NotContain("clinical narrative",
            "the summary is an inventory of what came back, not its content");
    }

    [Fact]
    public void The_request_is_built_with_what_the_operation_requires_and_no_more()
    {
        var request = SyntheticInteropData.DtrQuestionnairePackageRequest(Canonical);

        ParametersExtractor.PartNames(request).Should().BeEquivalentTo(["coverage", "questionnaire"]);
        ParametersExtractor.Resource<Coverage>(request, "coverage")!.SubscriberId
            .Should().Be(SyntheticInteropData.MemberId,
                "the payer looks a member up by identifier rather than trusting a sender-supplied reference");
        request.Parameter.Single(p => p.Name == "questionnaire").Value
            .Should().BeOfType<Canonical>().Which.Value.Should().Be(Canonical);
    }

    [Fact]
    public void The_request_round_trips_through_the_fhir_serializer_cho_uses()
    {
        var json = new FhirJsonSerializer().SerializeToString(
            SyntheticInteropData.DtrQuestionnairePackageRequest(Canonical));

        var parsed = new FhirJsonParser().Parse<Parameters>(json);

        ParametersExtractor.Resource<Coverage>(parsed, "coverage").Should().NotBeNull();
    }
}
