using CloudHealthOffice.Portal.Services.Dtr;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Tests.Services.Dtr;

/// <summary>
/// Hand entry of a DTR adaptive form, and the CQL-conditioned item the page
/// cannot evaluate: the questions asked so far come back from the plan with
/// their answers, the next round sends the answers on the page, and an item
/// shown by an enableWhenExpression is offered but never required.
/// </summary>
public class AdaptiveFormTests
{
    internal const string SleepSystem = "https://cloudhealthoffice.com/fhir/CodeSystem/dtr-sleep-study";
    internal const string SleepCanonical = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-sleep-study-adaptive|1.0.0";

    /// <summary>A response as $next-question returns it, holding the questions asked so far.</summary>
    internal static QuestionnaireResponse Round(
        IEnumerable<Questionnaire.ItemComponent> asked,
        IEnumerable<QuestionnaireResponse.ItemComponent>? answers = null,
        bool completed = false) => new()
    {
        Id = "draft-1",
        Contained =
        [
            new Questionnaire
            {
                Id = "adaptive",
                Title = "Prior Authorization: Attended Sleep Study (adaptive)",
                Status = PublicationStatus.Active,
                DerivedFrom = [SleepCanonical],
                Item = asked.ToList(),
            },
        ],
        Questionnaire = "#adaptive",
        Status = completed
            ? QuestionnaireResponse.QuestionnaireResponseStatus.Completed
            : QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
        Subject = new ResourceReference("Patient/MBR-1001"),
        Extension = [new Extension("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/qr-coverage", new ResourceReference("Coverage/cov-1"))],
        Item = (answers ?? []).ToList(),
    };

    internal static Questionnaire.ItemComponent HsatDone() => new()
    {
        LinkId = "hsat-done",
        Text = "Has the patient completed a home sleep apnea test (HSAT) for the current complaint?",
        Type = Questionnaire.QuestionnaireItemType.Boolean,
        Required = true,
    };

    internal static Questionnaire.ItemComponent Contraindication() => new()
    {
        LinkId = "hsat-contraindication",
        Text = "Why is home sleep apnea testing not appropriate for this patient?",
        Type = Questionnaire.QuestionnaireItemType.Choice,
        Required = true,
        AnswerOption =
        [
            new Questionnaire.AnswerOptionComponent { Value = new Coding(SleepSystem, "cardiopulmonary", "Significant cardiopulmonary disease") },
            new Questionnaire.AnswerOptionComponent { Value = new Coding(SleepSystem, "neuromuscular", "Neuromuscular disease affecting respiration") },
        ],
    };

    internal static QuestionnaireResponse.ItemComponent Answered(string linkId, DataType value) => new()
    {
        LinkId = linkId,
        Answer = [new QuestionnaireResponse.AnswerComponent { Value = value }],
    };

    [Fact]
    public void A_draft_that_names_a_contained_questionnaire_is_adaptive()
    {
        AdaptiveForm.IsAdaptiveDraft(Round([])).Should().BeTrue();
        AdaptiveForm.IsAdaptiveDraft(new QuestionnaireResponse { Questionnaire = SleepCanonical }).Should().BeFalse();
        AdaptiveForm.IsAdaptiveDraft(null).Should().BeFalse();
    }

    [Fact]
    public void The_questions_asked_so_far_come_back_with_their_answers()
    {
        var adaptive = new AdaptiveForm(Round(
            [HsatDone(), Contraindication()],
            [Answered("hsat-done", new FhirBoolean(false))]));

        adaptive.Form.Items.Select(i => i.LinkId).Should().Equal("hsat-done", "hsat-contraindication");
        adaptive.Form.Find("hsat-done")!.Boolean.Should().BeFalse();
        adaptive.Form.Find("hsat-contraindication")!.HasAnswer.Should().BeFalse();
        adaptive.Completed.Should().BeFalse();
    }

    [Fact]
    public void The_next_round_sends_the_answers_on_the_page_in_the_plans_response()
    {
        var adaptive = new AdaptiveForm(Round(
            [HsatDone(), Contraindication()],
            [Answered("hsat-done", new FhirBoolean(false))]));
        adaptive.Form.Find("hsat-contraindication")!.Choice = $"{SleepSystem}|cardiopulmonary";

        var next = adaptive.NextRequest(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

        next.Questionnaire.Should().Be("#adaptive");
        next.Contained.Should().ContainSingle().Which.Should().BeOfType<Questionnaire>()
            .Which.Item.Select(i => i.LinkId).Should().Equal("hsat-done", "hsat-contraindication");
        next.Status.Should().Be(QuestionnaireResponse.QuestionnaireResponseStatus.InProgress);
        next.Extension.Should().ContainSingle(e => e.Url.EndsWith("qr-coverage"), "the draft's extensions travel with it");
        next.Item.Select(i => i.LinkId).Should().Equal("hsat-done", "hsat-contraindication");
        ((Coding)next.Item[1].Answer.Single().Value).Code.Should().Be("cardiopulmonary");
        next.Authored.Should().Be("2026-11-03T12:00:00Z");
    }

    [Fact]
    public void A_response_without_its_contained_questionnaire_is_refused()
    {
        var broken = Round([]);
        broken.Contained.Clear();

        var act = () => new AdaptiveForm(broken);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_item_shown_by_cql_is_offered_but_never_required()
    {
        var questionnaire = new Questionnaire
        {
            Url = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri",
            Version = "1.0.0",
            Item =
            [
                new Questionnaire.ItemComponent
                {
                    LinkId = "3a",
                    Text = "Type and duration of the conservative treatment shown in the patient's record",
                    Type = Questionnaire.QuestionnaireItemType.String,
                    Required = true,
                    Extension =
                    [
                        new Extension(QuestionnaireForm.EnableWhenExpressionExtension, new Expression
                        {
                            Language = "text/cql-identifier",
                            Expression_ = "\"ChoImagingMriPrepopulation\".ConservativeTreatmentAttempted",
                        }),
                    ],
                },
            ],
        };
        var form = new QuestionnaireForm(questionnaire);
        var item = form.Find("3a")!;

        item.ConditionNotEvaluated.Should().BeTrue();
        form.IsEnabled(item).Should().BeTrue();
        item.Required.Should().BeFalse();
        form.Validate().Should().BeEmpty();

        item.Text = "6 weeks of physical therapy";
        form.ToResponse(new ResourceReference("Patient/MBR-1001"), null, DateTimeOffset.UtcNow)
            .Item.Single().LinkId.Should().Be("3a");
    }
}
