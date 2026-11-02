using CloudHealthOffice.Portal.Services.Dtr;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.Portal.Tests.Services.Dtr;

/// <summary>
/// The hand-entry DTR form: what is shown and required follows enableWhen,
/// answer lists come from answerOption or the package's expanded ValueSets,
/// and the response holds only enabled, answered items, nested as the form
/// nests them. No CQL is involved anywhere (Q-6).
/// </summary>
public class QuestionnaireFormTests
{
    private static QuestionnaireForm Form() => new(DtrFixtures.FormDefinition(), [DtrFixtures.ReasonsValueSet()]);

    [Fact]
    public void Hidden_items_are_not_on_the_form()
        => Form().Find("4").Should().BeNull();

    [Fact]
    public void An_item_is_enabled_only_when_its_condition_holds()
    {
        var form = Form();
        var flow = form.Find("1a")!;

        form.IsEnabled(flow).Should().BeFalse();
        form.Find("1")!.Choice = $"{DtrFixtures.Hcpcs}|E0601";
        form.IsEnabled(flow).Should().BeFalse();
        form.Find("1")!.Choice = $"{DtrFixtures.Hcpcs}|E1390";
        form.IsEnabled(flow).Should().BeTrue();
    }

    [Fact]
    public void Required_questions_are_required_only_while_enabled()
    {
        var form = Form();
        form.Validate().Should().BeEquivalentTo(["Equipment: an answer is required.", "Diagnosis summary: an answer is required."]);

        form.Find("1")!.Choice = $"{DtrFixtures.Hcpcs}|E1390";
        form.Find("2.1")!.Text = "Resting SpO2 86%";
        form.Validate().Should().ContainSingle().Which.Should().Be("Flow rate (L/min): an answer is required.");

        form.Find("1a")!.Number = "two";
        form.Validate().Should().ContainSingle().Which.Should().Contain("enter a number");

        form.Find("1a")!.Number = "2.5";
        form.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Answer_lists_come_from_answer_options_and_expanded_value_sets()
    {
        var form = Form();

        form.Find("1")!.Options.Select(o => o.Display).Should().Equal("Oxygen concentrator", "CPAP");
        form.Find("2.3")!.Options.Select(o => o.Display).Should().Equal("Hypoxemia", "COPD");
    }

    [Fact]
    public void Static_initial_values_are_applied()
        => Form().Find("3")!.Boolean.Should().BeFalse();

    [Fact]
    public void The_response_holds_enabled_answered_items_nested_like_the_form()
    {
        var form = Form();
        form.Find("1")!.Choice = $"{DtrFixtures.Hcpcs}|E0601";
        form.Find("1a")!.Number = "3"; // disabled: must not be sent
        form.Find("2.1")!.Text = "  Obstructive sleep apnea  ";
        form.Find("2.3")!.Choices.Add("http://snomed.info/sct|2");
        form.Find("2.3")!.Choices.Add("http://snomed.info/sct|1");

        var extension = new Extension("http://hl7.org/fhir/StructureDefinition/questionnaireresponse-coverage", new ResourceReference("Coverage/cov-1"));
        var response = form.ToResponse(new ResourceReference("Patient/MBR-1001"), [extension], new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

        response.Questionnaire.Should().Be(DtrFixtures.Canonical);
        response.Status.Should().Be(QuestionnaireResponse.QuestionnaireResponseStatus.Completed);
        response.Subject.Reference.Should().Be("Patient/MBR-1001");
        response.Authored.Should().Be("2026-11-02T09:00:00Z");
        response.Extension.Should().ContainSingle().Which.Url.Should().EndWith("questionnaireresponse-coverage");

        response.Item.Select(i => i.LinkId).Should().Equal("1", "2", "3");
        ((Coding)response.Item[0].Answer.Single().Value).Code.Should().Be("E0601");
        var group = response.Item[1];
        group.Item.Select(i => i.LinkId).Should().Equal("2.1", "2.3");
        ((FhirString)group.Item[0].Answer.Single().Value).Value.Should().Be("Obstructive sleep apnea");
        group.Item[1].Answer.Select(a => ((Coding)a.Value).Display).Should().Equal("Hypoxemia", "COPD");
        ((FhirBoolean)response.Item[2].Answer.Single().Value).Value.Should().BeFalse();
    }

    [Fact]
    public void Initial_expressions_are_ignored_no_cql_runs()
    {
        var questionnaire = DtrFixtures.FormDefinition();
        questionnaire.Item[3].Initial.Clear();
        questionnaire.Item[3].Extension.Add(new Extension(
            "http://hl7.org/fhir/uv/sdc/StructureDefinition/sdc-questionnaire-initialExpression",
            new Expression { Language = "text/cql-identifier", Expression_ = "HomeAssessmentDone" }));

        new QuestionnaireForm(questionnaire).Find("3")!.Boolean.Should().BeNull();
    }

    [Fact]
    public void Exists_and_not_equal_conditions()
    {
        var questionnaire = DtrFixtures.FormDefinition();
        questionnaire.Item[1].EnableWhen[0].Operator = Questionnaire.QuestionnaireItemOperator.Exists;
        questionnaire.Item[1].EnableWhen[0].Answer = new FhirBoolean(true);
        var form = new QuestionnaireForm(questionnaire);
        form.IsEnabled(form.Find("1a")!).Should().BeFalse();
        form.Find("1")!.Choice = $"{DtrFixtures.Hcpcs}|E0601";
        form.IsEnabled(form.Find("1a")!).Should().BeTrue();

        questionnaire = DtrFixtures.FormDefinition();
        questionnaire.Item[1].EnableWhen[0].Operator = Questionnaire.QuestionnaireItemOperator.NotEqual;
        form = new QuestionnaireForm(questionnaire);
        form.IsEnabled(form.Find("1a")!).Should().BeTrue();
        form.Find("1")!.Choice = $"{DtrFixtures.Hcpcs}|E1390";
        form.IsEnabled(form.Find("1a")!).Should().BeFalse();
    }

    [Fact]
    public void Focus_codes_offer_the_requested_service()
        => Form().FocusCodes.Should().ContainSingle().Which.Code.Should().Be("E1390");
}
