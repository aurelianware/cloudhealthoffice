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
/// DTR 2.2.0 <c>Questionnaire/$questionnaire-package</c>, end to end through the
/// real DtrService over the in-process store, against the requirements the DTR
/// payer-server kit checks: the output Parameters/package Bundle shape, the draft
/// QuestionnaireResponse, version-specific dependencies with CQL and ELM, and the
/// warning-versus-4xx split.
/// </summary>
public class QuestionnairePackageTests
{
    private const string Base = "https://test.example.com/fhir/r4";
    private const string Tenant = "tenant-a";
    private const string MriCanonical = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri";
    private const string MriLibrary = "https://cloudhealthoffice.com/fhir/Library/ChoImagingMriPrepopulation|1.0.0";
    private const string Cpt = "http://www.ama-assn.org/go/cpt";

    private readonly InMemoryDtrResourceStore _store = new();
    private readonly DtrService _service;

    public QuestionnairePackageTests()
    {
        _service = new DtrService(Options.Create(new DtrConfig()), _store, NullLogger<DtrService>.Instance);
    }

    private static Coverage Coverage() => new()
    {
        Id = "cov-1",
        Status = FinancialResourceStatusCodes.Active,
        Beneficiary = new ResourceReference("Patient/pat-1"),
        Payor = [new ResourceReference("Organization/payer-1")],
    };

    private static ServiceRequest LumbarMri() => new()
    {
        Id = "sr-1",
        Status = RequestStatus.Draft,
        Intent = RequestIntent.Order,
        Code = new CodeableConcept(Cpt, "72148", "MRI lumbar spine without contrast"),
        Subject = new ResourceReference("Patient/pat-1"),
    };

    private Task<QuestionnairePackageResult> Package(
        IEnumerable<Resource>? orders = null, IEnumerable<string>? questionnaires = null,
        string? context = null, DateTimeOffset? changedSince = null, Coverage? coverage = null)
        => _service.GetQuestionnairePackageAsync(
            new QuestionnairePackageRequest(
                coverage ?? Coverage(),
                (orders ?? []).ToList(),
                (questionnaires ?? []).ToList(),
                context,
                changedSince),
            Tenant, Base);

    private static IReadOnlyList<Bundle> Bundles(QuestionnairePackageResult result)
        => result.Package!.Parameter.Where(p => p.Name == "packagebundle").Select(p => (Bundle)p.Resource).ToList();

    private static OperationOutcome? Outcome(QuestionnairePackageResult result)
        => result.Package!.Parameter.SingleOrDefault(p => p.Name == "outcome")?.Resource as OperationOutcome;

    // ── Selection by order ──────────────────────────────────────────────────

    [Fact]
    public async Task An_mri_order_selects_the_mri_form_and_its_whole_package()
    {
        var result = await Package(orders: [LumbarMri()]);

        result.Status.Should().Be(200);
        result.Package!.Meta.Profile.Should().Contain(QuestionnairePackageBuilder.OutputParametersProfile);
        var bundle = Bundles(result).Should().ContainSingle().Subject;

        bundle.Type.Should().Be(Bundle.BundleType.Collection);
        bundle.Meta.Profile.Should().Contain(QuestionnairePackageBuilder.PackageBundleProfile);

        // dtrb-1 / oper-12: the Questionnaire first, exactly one draft response.
        var questionnaire = bundle.Entry[0].Resource.Should().BeOfType<Questionnaire>().Subject;
        questionnaire.Url.Should().Be(MriCanonical);
        questionnaire.Version.Should().Be("1.0.0");
        bundle.Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Should().ContainSingle();

        // oper-12/13: the Library it names, carrying both CQL and ELM.
        var library = bundle.Entry.Select(e => e.Resource).OfType<Library>().Should().ContainSingle().Subject;
        $"{library.Url}|{library.Version}".Should().Be(MriLibrary);
        library.Content.Select(c => c.ContentType).Should().BeEquivalentTo("text/cql", "application/elm+json");
        library.Content.Should().OnlyContain(c => c.Data != null && c.Data.Length > 0);

        // oper-14/15: both value sets the Library depends on, expanded.
        var valueSets = bundle.Entry.Select(e => e.Resource).OfType<ValueSet>().ToList();
        valueSets.Select(v => v.Url).Should().BeEquivalentTo(
            "https://cloudhealthoffice.com/fhir/ValueSet/dtr-radiculopathy",
            "https://cloudhealthoffice.com/fhir/ValueSet/dtr-physical-therapy");
        valueSets.Should().OnlyContain(v => v.Expansion != null && v.Expansion.Total == v.Expansion.Contains.Count
                                            && v.Expansion.Contains.Count > 0);

        Outcome(result).Should().BeNull("nothing went wrong");
    }

    [Fact]
    public async Task The_draft_response_references_only_what_the_client_sent()
    {
        var result = await Package(orders: [LumbarMri()]);
        var bundle = Bundles(result).Single();
        var qr = bundle.Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();

        qr.Status.Should().Be(QuestionnaireResponse.QuestionnaireResponseStatus.InProgress);
        qr.Meta.Profile.Should().Contain(QuestionnairePackageBuilder.QuestionnaireResponseProfile);
        // spec-122: the same canonical as the packaged Questionnaire, version-specific (oper-16).
        qr.Questionnaire.Should().Be($"{MriCanonical}|1.0.0");
        // spec-139: references resolve on the DTR client's own endpoint.
        qr.Subject.Reference.Should().Be("Patient/pat-1");
        Reference(qr, QuestionnairePackageBuilder.QrCoverageExtension).Should().Equal("Coverage/cov-1");
        Reference(qr, QuestionnairePackageBuilder.QrContextExtension).Should().Equal("ServiceRequest/sr-1");
        qr.Extension.Should().ContainSingle(e => e.Url == QuestionnairePackageBuilder.IntendedUseExtension)
            .Which.Value.Should().BeOfType<CodeableConcept>()
            .Which.Coding.Should().ContainSingle(c => c.Code == "withpa");
        qr.Item.Should().BeEmpty("answers are the DTR client's to populate");
    }

    [Fact]
    public async Task Every_dependency_reference_in_the_package_is_version_specific()
    {
        var bundle = Bundles(await Package(orders: [LumbarMri()])).Single();
        var questionnaire = (Questionnaire)bundle.Entry[0].Resource;
        var library = bundle.Entry.Select(e => e.Resource).OfType<Library>().Single();

        questionnaire.Extension.Where(e => e.Url == QuestionnairePackageBuilder.CqfLibraryExtension)
            .Select(e => ((Canonical)e.Value).Value)
            .Should().OnlyContain(c => c.Contains('|'));
        library.RelatedArtifact.Select(r => r.Resource).Should().OnlyContain(r => r.Contains('|'));
    }

    [Fact]
    public async Task An_order_no_template_covers_returns_no_package_and_says_so()
    {
        var order = LumbarMri();
        order.Code = new CodeableConcept(Cpt, "99213");

        var result = await Package(orders: [order]);

        Bundles(result).Should().BeEmpty();
        Outcome(result)!.Issue.Should().ContainSingle(i => i.Severity == OperationOutcome.IssueSeverity.Information
                                                            && i.Diagnostics.Contains("99213"));
    }

    [Fact]
    public async Task An_order_without_a_code_is_a_warning()
    {
        var order = LumbarMri();
        order.Code = null;

        var result = await Package(orders: [order]);

        result.Status.Should().Be(200);
        Outcome(result)!.Issue.Should().ContainSingle(i => i.Severity == OperationOutcome.IssueSeverity.Warning);
    }

    // ── Selection by canonical ──────────────────────────────────────────────

    [Theory]
    [InlineData(MriCanonical)]
    [InlineData(MriCanonical + "|1.0.0")]
    [InlineData("q-imaging-mri")]
    [InlineData("Questionnaire/q-imaging-mri")]
    public async Task A_questionnaire_resolves_by_canonical_or_by_local_id(string reference)
    {
        var bundle = Bundles(await Package(questionnaires: [reference])).Should().ContainSingle().Subject;

        ((Questionnaire)bundle.Entry[0].Resource).Url.Should().Be(MriCanonical);
    }

    [Fact]
    public async Task A_version_this_server_does_not_have_is_a_warning_not_a_failure()
    {
        var result = await Package(questionnaires: [MriCanonical + "|9.9.9"]);

        result.Status.Should().Be(200, "oper-9: a requested questionnaire that cannot be found is a warning");
        Bundles(result).Should().BeEmpty();
        Outcome(result)!.Issue.Should().ContainSingle(i => i.Severity == OperationOutcome.IssueSeverity.Warning
                                                            && i.Code == OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task One_found_and_one_missing_returns_the_found_package_and_warns_about_the_other()
    {
        var result = await Package(questionnaires: [MriCanonical, "https://inferno.health/Questionnaire/does-not-exist"]);

        Bundles(result).Should().ContainSingle();
        Outcome(result)!.Issue.Should().ContainSingle(i => i.Diagnostics.Contains("does-not-exist"));
    }

    [Fact]
    public async Task The_same_questionnaire_asked_for_twice_is_packaged_once()
    {
        var result = await Package(orders: [LumbarMri()], questionnaires: [MriCanonical, "q-imaging-mri"]);

        Bundles(result).Should().ContainSingle("oper-6: each Questionnaire version is returned only once");
    }

    [Fact]
    public async Task A_form_stored_without_a_canonical_is_published_under_this_server()
    {
        // The four original forms predate DTR canonicals.
        var bundle = Bundles(await Package(questionnaires: ["q-dme-equipment"])).Single();
        var questionnaire = (Questionnaire)bundle.Entry[0].Resource;
        var qr = bundle.Entry.Select(e => e.Resource).OfType<QuestionnaireResponse>().Single();

        questionnaire.Url.Should().Be($"{Base}/Questionnaire/q-dme-equipment");
        questionnaire.Version.Should().NotBeNullOrEmpty();
        qr.Questionnaire.Should().Be($"{questionnaire.Url}|{questionnaire.Version}");

        // ...and that canonical resolves back, so the completed response is accepted.
        (await _service.QuestionnaireExistsAsync(qr.Questionnaire, Tenant, Base)).Should().BeTrue();
    }

    // ── changedsince ────────────────────────────────────────────────────────

    [Fact]
    public async Task Changedsince_before_the_content_changed_returns_the_package()
    {
        var result = await Package(orders: [LumbarMri()], changedSince: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Bundles(result).Should().ContainSingle();
    }

    [Fact]
    public async Task Changedsince_after_the_content_changed_returns_nothing_and_says_so()
    {
        var result = await Package(orders: [LumbarMri()], changedSince: DateTimeOffset.UtcNow);

        result.Status.Should().Be(200);
        Bundles(result).Should().BeEmpty();
        Outcome(result)!.Issue.Should().ContainSingle(i => i.Diagnostics.Contains("changed"));
    }

    // ── Unusable source data: 4xx with an OperationOutcome (spec-130) ───────

    [Fact]
    public async Task A_context_id_alone_cannot_be_resolved_and_is_refused()
    {
        var result = await Package(context: "crd-assertion-123");

        result.Package.Should().BeNull();
        result.Status.Should().Be(422);
        result.Rejection!.Issue.Should().ContainSingle(i => i.Code == OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task An_unknown_context_id_is_refused_even_with_an_order()
    {
        // spec-130: "the context id can't be found" is a 4xx, whatever else the
        // request carries. CrdDtrLinkTests covers ids CRD issued.
        var result = await Package(orders: [LumbarMri()], context: "crd-assertion-123");

        result.Status.Should().Be(422);
        result.Rejection!.Issue.Should().ContainSingle(i => i.Code == OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task A_coverage_without_an_id_is_refused()
    {
        var coverage = Coverage();
        coverage.Id = null;

        var result = await Package(orders: [LumbarMri()], coverage: coverage);

        result.Status.Should().Be(400);
        result.Rejection!.Issue.Single().Diagnostics.Should().Contain("coverage.id");
    }

    [Fact]
    public async Task A_coverage_without_a_beneficiary_is_refused()
    {
        var coverage = Coverage();
        coverage.Beneficiary = null;

        (await Package(orders: [LumbarMri()], coverage: coverage)).Status.Should().Be(400);
    }

    // ── Tenant visibility ───────────────────────────────────────────────────

    [Fact]
    public async Task Another_tenants_form_is_never_packaged()
    {
        await _service.CreateQuestionnaireAsync(new Questionnaire
        {
            Id = "q-tenant-b-only",
            Url = "https://payer-b.example/Questionnaire/secret",
            Version = "1",
            Status = PublicationStatus.Active,
            Item = [new() { LinkId = "1", Text = "x", Type = Questionnaire.QuestionnaireItemType.String }],
        }, "tenant-b");

        var result = await Package(questionnaires: ["https://payer-b.example/Questionnaire/secret", "q-tenant-b-only"]);

        Bundles(result).Should().BeEmpty();
        Outcome(result)!.Issue.Should().HaveCount(2).And.OnlyContain(i => i.Code == OperationOutcome.IssueType.NotFound);
    }

    // ── Request parsing ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_requires_exactly_one_coverage()
    {
        var parameters = new Parameters();
        parameters.Add("questionnaire", new Canonical(MriCanonical));

        var (request, error) = QuestionnairePackageRequest.Parse(parameters);

        request.Should().BeNull();
        error.Should().Contain("coverage");
    }

    [Fact]
    public void Parse_enforces_dtr_1()
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "coverage", Resource = Coverage() });

        var (request, error) = QuestionnairePackageRequest.Parse(parameters);

        request.Should().BeNull();
        error.Should().Contain("dtr-1");
    }

    [Fact]
    public void Parse_refuses_an_order_of_a_type_the_operation_does_not_take()
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "coverage", Resource = Coverage() });
        parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "order", Resource = new Patient { Id = "p" } });

        QuestionnairePackageRequest.Parse(parameters).Error.Should().Contain("order");
    }

    [Fact]
    public void Parse_reads_every_input()
    {
        var parameters = new Parameters();
        parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "coverage", Resource = Coverage() });
        parameters.Parameter.Add(new Parameters.ParameterComponent { Name = "order", Resource = LumbarMri() });
        parameters.Add("questionnaire", new Canonical(MriCanonical + "|1.0.0"));
        parameters.Add("context", new FhirString("ctx-1"));
        parameters.Add("changedsince", new Instant(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)));

        var (request, error) = QuestionnairePackageRequest.Parse(parameters);

        error.Should().BeNull();
        request!.Coverage.Id.Should().Be("cov-1");
        request.Orders.Should().ContainSingle().Which.Should().BeOfType<ServiceRequest>();
        request.Questionnaires.Should().Equal(MriCanonical + "|1.0.0");
        request.Context.Should().Be("ctx-1");
        request.ChangedSince.Should().Be(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
    }

    private static IEnumerable<string> Reference(QuestionnaireResponse qr, string url)
        => qr.Extension.Where(e => e.Url == url).Select(e => ((ResourceReference)e.Value).Reference);
}
