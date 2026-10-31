using System.Text.Json;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.Testing.FhirService;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Dtr;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// CRD to DTR (G-44, G-56). A documentation-required answer names the DTR
/// questionnaire in <c>coverage-information</c> (CRD resp-38), CHO keeps the
/// assertion, and DTR <c>$questionnaire-package</c> resolves its
/// <c>coverage-assertion-id</c> as the <c>context</c> (DTR spec-107), refusing
/// one it cannot use with a 4xx (spec-130). Real CrdService and DtrService over
/// the in-process stores, so the two halves are tested against each other.
/// </summary>
public class CrdDtrLinkTests
{
    private const string Tenant = "tenant-crd-dtr";
    private const string Cpt = "http://www.ama-assn.org/go/cpt";
    private const string MriCanonical = "https://cloudhealthoffice.com/fhir/Questionnaire/pa-imaging-mri|1.0.0";
    private const string Base = "https://test.example.com/fhir/r4";

    private static readonly CrdConfig Config = new()
    {
        Enabled = true,
        AuthRequiredCodes = ["27447"],
        DocumentationRequiredCodes = ["72148", "97110"],
    };

    private readonly Mock<IPriorAuthRuleEngine> _engine = new();
    private readonly InMemoryCrdAssertionStore _assertions = new();
    private readonly DtrService _dtr;

    public CrdDtrLinkTests()
    {
        _engine
            .Setup(e => e.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "NoRuleMatch",
                ResolvedRuleSetKey = "platform/TX/Medicaid/STAR",
            });
        _dtr = new DtrService(Options.Create(new DtrConfig()), new InMemoryDtrResourceStore(),
            NullLogger<DtrService>.Instance, _assertions);
    }

    // ── CRD names the form ───────────────────────────────────────────────────

    [Fact]
    public async Task A_documentation_required_order_names_the_questionnaire_that_collects_it()
    {
        var extension = OnlyExtension(await Service().EvaluateCoverageRequirementsAsync(Hook("72148"), Tenant));

        Values(extension, "questionnaire", "valueCanonical").Should().Equal(MriCanonical);
        Values(extension, "doc-needed", "valueCode").Should().Equal("clinical"); // crd-ci-q1
    }

    [Fact]
    public async Task A_documentation_required_code_with_no_form_names_none()
    {
        // 97110 needs documentation, but no shipped questionnaire lists it.
        var extension = OnlyExtension(await Service().EvaluateCoverageRequirementsAsync(Hook("97110"), Tenant));

        Values(extension, "questionnaire", "valueCanonical").Should().BeEmpty();
        Values(extension, "doc-needed", "valueCode").Should().Equal("clinical");
    }

    [Fact]
    public async Task Other_determinations_never_name_a_questionnaire()
    {
        var extension = OnlyExtension(await Service().EvaluateCoverageRequirementsAsync(Hook("27447"), Tenant));

        Values(extension, "questionnaire", "valueCanonical").Should().BeEmpty();
    }

    [Fact]
    public void The_extension_drops_questionnaires_without_doc_needed()
    {
        // crd-ci-q1, whatever the caller sets.
        var extension = new CrdCoverageInformation
        {
            CoverageReference = "Coverage/c",
            CoverageAssertionId = "a",
            Date = new DateOnly(2026, 10, 31),
            Determination = CrdDetermination.AuthorizationRequired,
            Questionnaires = [MriCanonical],
        }.ToExtension();

        JsonSerializer.Serialize(extension).Should().NotContain("questionnaire");
    }

    // ── CRD keeps the assertion ──────────────────────────────────────────────

    [Fact]
    public async Task Each_assertion_is_kept_under_the_id_the_order_carries()
    {
        var result = await Service().EvaluateCoverageRequirementsAsync(Hook("72148"), Tenant);
        var id = Values(OnlyExtension(result), "coverage-assertion-id", "valueString").Single()!;

        var kept = await _assertions.FindAsync(Tenant, id, DateTimeOffset.UtcNow);

        kept.Should().NotBeNull();
        kept!.CoverageReference.Should().Be("Coverage/cov-001");
        kept.OrderReference.Should().Be("ServiceRequest/sr-001");
        kept.BillingCode.Should().Be("72148");
        kept.DocumentationNeeded.Should().BeTrue();
        kept.Questionnaires.Should().Equal(MriCanonical);
        kept.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(90), TimeSpan.FromMinutes(1));
        (await _assertions.FindAsync("another-tenant", id, DateTimeOffset.UtcNow)).Should().BeNull();
    }

    [Fact]
    public async Task Without_a_coverage_nothing_is_asserted_or_kept()
    {
        var request = Hook("72148");
        request.Prefetch = null;

        var result = await Service().EvaluateCoverageRequirementsAsync(request, Tenant);

        result.SystemActions.Should().BeEmpty();
        _assertions.All.Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_failure_still_answers_the_hook()
    {
        var failing = new Mock<ICrdAssertionStore>();
        failing.Setup(s => s.SaveAsync(It.IsAny<IReadOnlyList<CrdCoverageAssertion>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        var result = await Service(store: failing.Object).EvaluateCoverageRequirementsAsync(Hook("72148"), Tenant);

        result.SystemActions.Should().ContainSingle();
        result.Cards.Should().ContainSingle().Which.Summary.Should().Contain("Documentation required");
    }

    // ── The card ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Without_a_dtr_app_the_card_is_instructions_with_no_dead_link()
    {
        var card = (await Service().EvaluateCoverageRequirementsAsync(Hook("72148"), Tenant)).Cards.Single();

        card.Links.Should().BeEmpty();
        card.Suggestions.Should().BeEmpty();
        card.Indicator.Should().Be("warning");
        card.Detail.Should().Contain("DTR application").And.Contain("names the form");
    }

    [Fact]
    public async Task With_a_dtr_app_the_card_launches_it_with_the_forms_and_the_context()
    {
        var result = await Service(launchUrl: "https://dtr.example.org/launch")
            .EvaluateCoverageRequirementsAsync(Hook("72148"), Tenant);
        var card = result.Cards.Single();
        var id = Values(OnlyExtension(result), "coverage-assertion-id", "valueString").Single()!;

        // CRDHooksResponse-launchSMART: smart links, a suggestion, no actions.
        var link = card.Links.Should().ContainSingle().Subject;
        link.Type.Should().Be("smart");
        link.Url.Should().Be("https://dtr.example.org/launch");
        card.Suggestions.Should().ContainSingle();

        using var appContext = JsonDocument.Parse(link.AppContext!);
        var root = appContext.RootElement;
        root.GetProperty("questionnaire").EnumerateArray().Select(e => e.GetString()).Should().Equal(MriCanonical);
        root.GetProperty("context").GetString().Should().Be(id);
        root.GetProperty("order").GetString().Should().Be("ServiceRequest/sr-001");
        root.GetProperty("coverage").GetString().Should().Be("Coverage/cov-001");
    }

    [Fact]
    public void An_absolute_link_never_serializes_an_app_context()
    {
        // CDS Hooks: appContext only on smart links.
        JsonSerializer.Serialize(new CrdCardLink { Label = "x", Url = "https://x", Type = "absolute" })
            .Should().NotContain("appContext");
    }

    // ── DTR resolves the context ─────────────────────────────────────────────

    [Fact]
    public async Task The_assertion_id_alone_returns_the_package_crd_named()
    {
        var id = await AssertAsync("72148");

        var result = await Package(context: id);

        result.Status.Should().Be(200);
        var bundles = result.Package!.Parameter.Where(p => p.Name == "packagebundle").ToList();
        var q = ((Bundle)bundles.Should().ContainSingle().Subject.Resource).Entry[0].Resource.Should()
            .BeOfType<Questionnaire>().Subject;
        QuestionnairePackageBuilder.Canonical(q).Should().Be(MriCanonical);
    }

    [Fact]
    public async Task The_context_and_the_order_together_give_one_package()
    {
        var id = await AssertAsync("72148");

        var result = await Package(context: id, orders: [LumbarMri()]);

        result.Package!.Parameter.Count(p => p.Name == "packagebundle").Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_context_is_a_4xx()
    {
        var result = await Package(context: "not-an-assertion");

        result.Status.Should().Be(422);
        result.Rejection!.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.NotFound);
    }

    [Fact]
    public async Task Another_tenants_context_is_not_found()
    {
        var id = await AssertAsync("72148");

        var result = await _dtr.GetQuestionnairePackageAsync(
            new QuestionnairePackageRequest(Coverage(), [], [], id, null), "another-tenant", Base);

        result.Status.Should().Be(422);
    }

    [Fact]
    public async Task A_context_issued_for_another_coverage_is_refused()
    {
        var id = await AssertAsync("72148");
        var other = Coverage();
        other.Id = "cov-other";

        var result = await Package(context: id, coverage: other);

        result.Status.Should().Be(422);
        result.Rejection!.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.BusinessRule);
    }

    [Fact]
    public async Task A_context_that_asked_for_no_documentation_is_refused()
    {
        var id = await AssertAsync("27447");

        var result = await Package(context: id);

        result.Status.Should().Be(422);
        result.Rejection!.Issue.Single().Diagnostics.Should().Contain("did not indicate that documentation is needed");
    }

    [Fact]
    public async Task A_context_with_no_form_says_how_else_to_send_documentation()
    {
        var id = await AssertAsync("97110");

        var result = await Package(context: id);

        result.Status.Should().Be(200);
        result.Package!.Parameter.Should().NotContain(p => p.Name == "packagebundle");
        var outcome = (OperationOutcome)result.Package.Parameter.Single(p => p.Name == "outcome").Resource;
        outcome.Issue.Should().Contain(i => i.Severity == OperationOutcome.IssueSeverity.Warning
                                            && i.Diagnostics.Contains("prior authorization request"));
    }

    [Fact]
    public async Task An_expired_context_is_not_found()
    {
        await _assertions.SaveAsync(
        [
            new CrdCoverageAssertion
            {
                TenantId = Tenant,
                AssertionId = "old",
                CoverageReference = "Coverage/cov-1",
                Determination = CrdDetermination.DocumentationRequired,
                Questionnaires = [MriCanonical],
                AssertedAt = DateTimeOffset.UtcNow.AddDays(-100),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
            },
        ]);

        (await Package(context: "old")).Status.Should().Be(422);
    }

    [Theory]
    [InlineData("Coverage/cov-1", true)]
    [InlineData("https://ehr.example.org/fhir/Coverage/cov-1", true)]
    [InlineData("Coverage/cov-1/_history/3", true)]
    [InlineData("Coverage/cov-10", false)]
    [InlineData("Coverage/xcov-1", false)]
    public void Coverage_references_match_by_id(string reference, bool same)
        => DtrService.SameCoverage(reference, "cov-1").Should().Be(same);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private CrdService Service(string? launchUrl = null, ICrdAssertionStore? store = null) => new(
        new UnavailableTerminology(),
        _engine.Object,
        Options.Create(Config),
        new CrdClassificationStore(new MemoryCache(new MemoryCacheOptions { SizeLimit = 1024 })),
        CrdContextResolverDouble.Resolved(),
        NullLogger<CrdService>.Instance,
        _dtr,
        store ?? _assertions,
        Options.Create(new DtrConfig { DtrLaunchUrl = launchUrl }));

    /// <summary>Runs CRD for one code against Coverage/cov-1 and returns the assertion id.</summary>
    private async Task<string> AssertAsync(string code)
    {
        var result = await Service().EvaluateCoverageRequirementsAsync(Hook(code, "cov-1"), Tenant);
        return Values(OnlyExtension(result), "coverage-assertion-id", "valueString").Single()!;
    }

    private Task<QuestionnairePackageResult> Package(
        string? context = null, IEnumerable<Resource>? orders = null, Coverage? coverage = null)
        => _dtr.GetQuestionnairePackageAsync(
            new QuestionnairePackageRequest(coverage ?? Coverage(), (orders ?? []).ToList(), [], context, null),
            Tenant, Base);

    private static Coverage Coverage() => new()
    {
        Id = "cov-1",
        Status = FinancialResourceStatusCodes.Active,
        Beneficiary = new ResourceReference("Patient/pat-1"),
    };

    private static ServiceRequest LumbarMri() => new()
    {
        Id = "sr-1",
        Status = RequestStatus.Draft,
        Intent = RequestIntent.Order,
        Code = new CodeableConcept(Cpt, "72148"),
        Subject = new ResourceReference("Patient/pat-1"),
    };

    private static JsonElement OnlyExtension(CrdEvaluationResult result)
        => result.SystemActions.Should().ContainSingle().Subject.Resource!.Value
            .GetProperty("extension").EnumerateArray()
            .Single(e => e.GetProperty("url").GetString() == CrdCoverageInformation.ExtensionUrl);

    private static List<string?> Values(JsonElement extension, string url, string valueProperty)
        => extension.GetProperty("extension").EnumerateArray()
            .Where(p => p.GetProperty("url").GetString() == url)
            .Select(p => p.GetProperty(valueProperty).GetString())
            .ToList();

    private static CrdHookRequest Hook(string code, string coverageId = "cov-001") => new()
    {
        HookInstance = Guid.NewGuid().ToString(),
        Hook = "order-sign",
        Context = new CrdHookContext
        {
            UserId = "Practitioner/123",
            PatientId = "pat-001",
            DraftOrders = new CrdDraftOrders
            {
                Entry =
                [
                    new()
                    {
                        RawResource = JsonDocument.Parse($$"""
                            {
                              "resourceType": "ServiceRequest",
                              "id": "sr-001",
                              "status": "draft",
                              "intent": "order",
                              "code": { "coding": [ { "system": "{{Cpt}}", "code": "{{code}}" } ] },
                              "subject": { "reference": "Patient/pat-001" }
                            }
                            """).RootElement.Clone(),
                    },
                ],
            },
        },
        Prefetch = new Dictionary<string, JsonElement>
        {
            ["coverage"] = JsonDocument.Parse($$"""
                {
                  "resourceType": "Bundle",
                  "type": "searchset",
                  "entry": [
                    { "resource": { "resourceType": "Coverage", "id": "{{coverageId}}", "status": "active" } }
                  ]
                }
                """).RootElement.Clone(),
        },
    };

    private sealed class UnavailableTerminology : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new Handler()) { BaseAddress = new Uri("http://terminology.test/") };

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => throw new HttpRequestException("terminology-service unavailable");
        }
    }
}
