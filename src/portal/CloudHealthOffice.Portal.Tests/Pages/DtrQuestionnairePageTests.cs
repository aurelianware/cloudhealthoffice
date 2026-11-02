using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services.Dtr;
using CloudHealthOffice.Portal.Tests.Services.Dtr;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Task = System.Threading.Tasks.Task;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The provider portal's DTR page end to end against a mocked fhir-service:
/// a CRD link opens the named form for the member the provider looks up,
/// required answers are enforced, the completed response is saved, and the
/// PAS request carries it.
/// </summary>
public class DtrQuestionnairePageTests : TestContext
{
    private const string Text = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-text|1.0.0";

    private readonly Mock<IDtrPortalService> _dtr = new();
    private QuestionnaireResponse? _saved;
    private Bundle? _submitted;

    public DtrQuestionnairePageTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_dtr.Object);
        Services.AddSingleton(GatingUsers.With("authorizations:write").Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _dtr.Setup(d => d.FindMemberAsync("MBR-1001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DtrMember(DtrFixtures.Member(), [DtrFixtures.MemberCoverage()]));
        _dtr.Setup(d => d.GetPackageAsync(It.IsAny<Coverage>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<Coding?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DtrPackage([new DtrPackagedForm(TextForm(), [], [])], []));
        _dtr.Setup(d => d.SaveResponseAsync(It.IsAny<QuestionnaireResponse>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((QuestionnaireResponse r, CancellationToken _) =>
            {
                _saved = (QuestionnaireResponse)r.DeepCopy();
                _saved.Id = "qr-saved-1";
                return _saved;
            });
        _dtr.Setup(d => d.SubmitPriorAuthAsync(It.IsAny<Bundle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Bundle b, CancellationToken _) =>
            {
                _submitted = b;
                return new PasSubmitOutcome(true, "PAS-20261102-ABCD1234", "Pended for review", null, []);
            });
    }

    /// <summary>A form of free-text items only, so the test can type every answer.</summary>
    private static Questionnaire TextForm() => new()
    {
        Url = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-text",
        Version = "1.0.0",
        Title = "Prior authorization: crown",
        Status = PublicationStatus.Active,
        Item =
        [
            new Questionnaire.ItemComponent { LinkId = "1", Text = "Tooth number", Type = Questionnaire.QuestionnaireItemType.String, Required = true },
            new Questionnaire.ItemComponent { LinkId = "2", Text = "Notes", Type = Questionnaire.QuestionnaireItemType.String },
        ],
    };

    private IRenderedComponent<DtrQuestionnaire> RenderAt(string uri)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var cut = RenderComponent<DtrQuestionnaire>();
        // The permission gate resolves the user first.
        cut.WaitForElement("[data-testid=dtr-page]");
        return cut;
    }

    /// <summary>MudTextField puts data-testid on the input itself; a form item's is on its wrapper.</summary>
    private static void Type(IRenderedFragment cut, string selector, string value)
    {
        var element = cut.Find(selector);
        var input = element.TagName == "INPUT" ? element : element.QuerySelector("input")!;
        // Immediate fields bind on input, the others on change.
        input.Input(value);
        input.Change(value);
    }

    private IRenderedComponent<DtrQuestionnaire> OpenFromCrdLink()
    {
        var cut = RenderAt($"/dtr/questionnaire?questionnaire={Uri.EscapeDataString(Text)}&code={Uri.EscapeDataString("http://www.ada.org/cdt|D2740")}&context=abc");
        Type(cut, "[data-testid=dtr-member-id]", "MBR-1001");
        cut.Find("[data-testid=dtr-find-member]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dtr-form]").Should().ContainSingle());
        return cut;
    }

    [Fact]
    public void Without_a_link_the_provider_chooses_the_form_from_the_library()
    {
        _dtr.Setup(d => d.ListQuestionnairesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([TextForm()]);

        var cut = RenderAt("/dtr/questionnaire");
        Type(cut, "[data-testid=dtr-member-id]", "MBR-1001");
        cut.Find("[data-testid=dtr-find-member]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dtr-form-choice]").Should().ContainSingle());
        _dtr.Verify(d => d.GetPackageAsync(It.IsAny<Coverage>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<Coding?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_crd_link_opens_the_named_form_for_the_member_found()
    {
        var cut = OpenFromCrdLink();

        cut.Find("[data-testid=dtr-member-name]").TextContent.Should().Contain("Ana Rivera");
        cut.Find("[data-testid=dtr-form]").TextContent.Should().Contain("Prior authorization: crown").And.Contain("Tooth number");
        _dtr.Verify(d => d.GetPackageAsync(
            It.Is<Coverage>(c => c.Id == "cov-1" && c.Beneficiary.Reference == "Patient/MBR-1001"),
            It.Is<IReadOnlyList<string>>(q => q.SequenceEqual(new[] { Text })),
            null, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void An_unknown_member_is_said_so()
    {
        var cut = RenderAt("/dtr/questionnaire");
        Type(cut, "[data-testid=dtr-member-id]", "NOPE");
        cut.Find("[data-testid=dtr-find-member]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dtr-member-not-found]").Should().ContainSingle());
    }

    [Fact]
    public void A_required_answer_must_be_given_before_saving()
    {
        var cut = OpenFromCrdLink();

        cut.Find("[data-testid=dtr-save]").Click();

        cut.Find("[data-testid=dtr-form-problem]").TextContent.Should().Contain("Tooth number");
        _dtr.Verify(d => d.SaveResponseAsync(It.IsAny<QuestionnaireResponse>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Saved_answers_go_with_the_prior_authorization_request()
    {
        var cut = OpenFromCrdLink();
        Type(cut, "[data-testid=dtr-item-1]", "30");
        cut.Find("[data-testid=dtr-save]").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=dtr-pa-section]").Should().ContainSingle());
        _saved!.Subject.Reference.Should().Be("Patient/MBR-1001");
        _saved.Questionnaire.Should().Be(Text);
        ((FhirString)_saved.Item.Single().Answer.Single().Value).Value.Should().Be("30");

        // The CRD link's service code is offered and chosen.
        var entry = (PriorAuthEntry)typeof(DtrQuestionnaire)
            .GetField("_entry", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        entry.ServiceCode.Should().Be("D2740");
        entry.ServiceSystem.Should().Be(PasRequestBuilder.CdtSystem);

        // No date yet: the page says so and sends nothing.
        Type(cut, "[data-testid=dtr-npi]", "1234567893");
        cut.Find("[data-testid=dtr-submit]").Click();
        cut.FindAll("[data-testid=dtr-submit-problem]").Select(p => p.TextContent).Should().Equal("Enter the planned service date.");
        _submitted.Should().BeNull();

        // Enter the date through the page's own entry model (the date picker is JS-driven).
        entry.ServiceDateFrom = new DateTime(2026, 11, 20);
        await cut.InvokeAsync(() => cut.Find("[data-testid=dtr-submit]").Click());

        cut.WaitForAssertion(() => cut.Find("[data-testid=dtr-submitted]").TextContent.Should().Contain("PAS-20261102-ABCD1234"));
        var claim = _submitted!.Entry.Select(e => e.Resource).OfType<Claim>().Single();
        claim.Item.Single().ProductOrService.Coding.Single().Code.Should().Be("D2740");
        ((ResourceReference)claim.SupportingInfo.Single().Value).Reference.Should().Be("QuestionnaireResponse/qr-saved-1");
    }
}
