using CloudHealthOffice.Testing.FhirService;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Dtr;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.FhirService.Tests.Services.Dtr;

/// <summary>
/// DTR adaptive forms: the package an adaptive form arrives in, and
/// <c>$next-question</c> walking the shipped sleep-study form the way the
/// Inferno DTR payer-server kit does (submit, take the new question, answer it,
/// submit again) until the response comes back completed.
/// </summary>
public class AdaptiveQuestionnaireEngineTests
{
    private const string Base = "https://test.example.com/fhir/r4";
    private const string Url = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-sleep-study-adaptive";
    private const string Canonical = Url + "|1.0.0";
    private const string SleepSystem = "https://cloudhealthoffice.com/fhir/CodeSystem/dtr-sleep-study";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly AdaptiveQuestionnaireEngine _engine = new(DtrContentLibrary.Default);

    private static async Task<Bundle> AdaptivePackage()
    {
        var service = new DtrService(
            Options.Create(new DtrConfig()), new InMemoryDtrResourceStore(), NullLogger<DtrService>.Instance);
        var result = await service.GetQuestionnairePackageAsync(
            new QuestionnairePackageRequest(
                new Coverage
                {
                    Id = "cov-1",
                    Status = FinancialResourceStatusCodes.Active,
                    Beneficiary = new ResourceReference("Patient/pat-1"),
                    Payor = [new ResourceReference("Organization/payer-1")],
                },
                [],
                [Canonical],
                null,
                null),
            "tenant-a", Base);
        result.Status.Should().Be(200);
        return result.Package!.Parameter.Where(p => p.Name == "packagebundle").Select(p => (Bundle)p.Resource)
            .Should().ContainSingle().Subject;
    }

    private static QuestionnaireResponse Answer(QuestionnaireResponse response, string linkId, DataType value)
    {
        var next = (QuestionnaireResponse)response.DeepCopy();
        next.Item.Add(new QuestionnaireResponse.ItemComponent
        {
            LinkId = linkId,
            Answer = [new QuestionnaireResponse.AnswerComponent { Value = value }],
        });
        return next;
    }

    private static IEnumerable<string> Asked(QuestionnaireResponse response)
        => response.Contained.OfType<Questionnaire>().Single().Item.Select(i => i.LinkId);

    [Fact]
    public async Task Versioned_adaptive_questionnaires_carry_a_version_algorithm_sdc_2()
    {
        var bundle = await AdaptivePackage();
        var form = bundle.Entry.Select(e => e.Resource).OfType<Questionnaire>().Single();
        var draft = bundle.Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();

        static void HasSemver(Questionnaire q) =>
            q.GetExtension(AdaptiveQuestionnaireEngine.VersionAlgorithmExtension)!.Value
                .Should().BeOfType<Coding>().Which.Code.Should().Be("semver");

        HasSemver(form);
        HasSemver(draft.Contained.OfType<Questionnaire>().Single());

        // A client's contained form without it gets one back from $next-question.
        draft.Contained.OfType<Questionnaire>().Single().Extension
            .RemoveAll(e => e.Url == AdaptiveQuestionnaireEngine.VersionAlgorithmExtension);
        var result = _engine.Next(draft, Now);
        HasSemver(result.Response!.Contained.OfType<Questionnaire>().Single());

        draft.Extension.Should().ContainSingle(e => e.Url == QuestionnairePackageBuilder.IntendedUseExtension)
            .Which.Value.Should().BeOfType<CodeableConcept>().Which.Coding.Should().ContainSingle(c =>
                c.System == QuestionnairePackageBuilder.CrdCoverageInformationCodeSystem && c.Code == "withpa");
    }

    [Fact]
    public async Task The_package_carries_the_search_form_and_a_response_with_an_empty_contained_questionnaire()
    {
        var bundle = await AdaptivePackage();

        var form = bundle.Entry[0].Resource.Should().BeOfType<Questionnaire>().Subject;
        form.Meta.Profile.Should().Contain(DtrContentLibrary.AdaptiveSearchProfile);
        form.Item.Should().BeEmpty();
        form.Extension.Should().ContainSingle(e => e.Url == DtrContentLibrary.QuestionnaireAdaptiveExtension)
            .Which.Value.Should().BeOfType<FhirUrl>().Which.Value.Should().Be(Base, "spec-24: a sub-URL of the payer base");

        var response = bundle.Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Should().ContainSingle().Subject;
        response.Meta.Profile.Should().Contain(AdaptiveQuestionnaireEngine.AdaptiveResponseProfile);
        response.Questionnaire.Should().Be("#adaptive");
        var contained = response.Contained.Should().ContainSingle().Which.Should().BeOfType<Questionnaire>().Subject;
        contained.Id.Should().Be("adaptive");
        contained.DerivedFrom.Should().Contain(Canonical, "spec-25: derived from the adaptive form's canonical");
        contained.Item.Should().BeEmpty();
        contained.Meta.Profile.Should().Contain(AdaptiveQuestionnaireEngine.AdaptiveQuestionnaireProfile);
    }

    [Fact]
    public async Task Next_question_walks_the_form_one_question_at_a_time_to_completion()
    {
        var response = (await AdaptivePackage()).Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();

        var first = _engine.Next(response, Now);
        first.Status.Should().Be(200);
        Asked(first.Response!).Should().Equal("hsat-done");
        first.Response!.Status.Should().Be(QuestionnaireResponse.QuestionnaireResponseStatus.InProgress);

        // No home test: the contraindication question, never the HSAT result.
        var second = _engine.Next(Answer(first.Response, "hsat-done", new FhirBoolean(false)), Now);
        Asked(second.Response!).Should().Equal("hsat-done", "hsat-contraindication");

        var third = _engine.Next(
            Answer(second.Response!, "hsat-contraindication", new Coding(SleepSystem, "cardiopulmonary")), Now);
        Asked(third.Response!).Should().Equal("hsat-done", "hsat-contraindication", "ess-score");

        var done = _engine.Next(Answer(third.Response!, "ess-score", new Integer(14)), Now);
        done.Status.Should().Be(200);
        done.Response!.Status.Should().Be(QuestionnaireResponse.QuestionnaireResponseStatus.Completed);
        Asked(done.Response).Last().Should().Be(AdaptiveQuestionnaireEngine.CompletionLinkId, "spec-39");
        done.Response.Item.Select(i => i.LinkId).Should().Equal("hsat-done", "hsat-contraindication", "ess-score");
    }

    [Fact]
    public async Task An_unanswered_required_question_is_returned_unchanged()
    {
        var response = (await AdaptivePackage()).Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();
        var first = _engine.Next(response, Now).Response!;

        var again = _engine.Next(first, Now);

        again.Status.Should().Be(200);
        Asked(again.Response!).Should().Equal("hsat-done");
    }

    [Fact]
    public async Task An_answer_of_the_wrong_type_is_a_400_with_an_operation_outcome()
    {
        var response = (await AdaptivePackage()).Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();
        var first = _engine.Next(response, Now).Response!;

        var result = _engine.Next(Answer(first, "hsat-done", new FhirString("probably not")), Now);

        result.Status.Should().Be(400, "spec-147");
        result.Response.Should().BeNull();
        result.Rejection!.Issue.Should().ContainSingle()
            .Which.Diagnostics.Should().Contain("hsat-done").And.Contain("boolean");
    }

    [Fact]
    public async Task An_answer_outside_the_options_is_invalid()
    {
        var response = (await AdaptivePackage()).Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();
        var second = _engine.Next(Answer(_engine.Next(response, Now).Response!, "hsat-done", new FhirBoolean(false)), Now)
            .Response!;

        var result = _engine.Next(Answer(second, "hsat-contraindication", new Coding(SleepSystem, "no-such-code")), Now);

        result.Status.Should().Be(400);
    }

    [Fact]
    public async Task An_answer_to_a_question_the_payer_never_asked_is_invalid()
    {
        var response = (await AdaptivePackage()).Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();

        var result = _engine.Next(Answer(response, "ess-score", new Integer(10)), Now);

        result.Status.Should().Be(400);
    }

    [Fact]
    public async Task A_contained_question_the_payer_did_not_issue_is_invalid()
    {
        var response = (await AdaptivePackage()).Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();
        response.Contained.OfType<Questionnaire>().Single().Item.Add(new Questionnaire.ItemComponent
        {
            LinkId = "invented",
            Type = Questionnaire.QuestionnaireItemType.String,
            Text = "Invented",
        });

        _engine.Next(response, Now).Status.Should().Be(400);
    }

    [Fact]
    public void A_response_without_a_contained_questionnaire_is_invalid()
    {
        var result = _engine.Next(new QuestionnaireResponse
        {
            Questionnaire = Canonical,
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
        }, Now);

        result.Status.Should().Be(400);
    }

    [Fact]
    public void A_form_this_payer_does_not_publish_is_refused()
    {
        var result = _engine.Next(new QuestionnaireResponse
        {
            Contained = [new Questionnaire { Id = "adaptive", Url = "https://elsewhere.example/Questionnaire/x" }],
            Questionnaire = "#adaptive",
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
        }, Now);

        result.Status.Should().Be(422);
    }
}
