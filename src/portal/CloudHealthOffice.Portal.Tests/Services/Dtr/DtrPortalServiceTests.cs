using System.Net;
using System.Text;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Services.Dtr;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Task = System.Threading.Tasks.Task;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.Portal.Tests.Services.Dtr;

/// <summary>The questionnaire page's calls to fhir-service, and how it reads the answers.</summary>
public class DtrPortalServiceTests
{
    private static readonly FhirJsonSerializer Serializer = new();

    private static HttpResponseMessage Fhir(HttpStatusCode status, Resource resource) => new(status)
    {
        Content = new StringContent(Serializer.SerializeToString(resource), Encoding.UTF8, "application/fhir+json"),
    };

    private static DtrPortalService Service(FakeHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Services:FhirService"] = "http://fhir.test" }).Build(),
        NullLogger<DtrPortalService>.Instance);

    [Fact]
    public async Task FindMember_reads_the_patient_and_their_coverages()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath switch
        {
            "/fhir/r4/Patient/MBR-1001" => Fhir(HttpStatusCode.OK, DtrFixtures.Member()),
            "/fhir/r4/Coverage" => Fhir(HttpStatusCode.OK, new Bundle
            {
                Type = Bundle.BundleType.Searchset,
                Entry = [new Bundle.EntryComponent { Resource = DtrFixtures.MemberCoverage() }],
            }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var member = await Service(handler).FindMemberAsync(" MBR-1001 ");

        member!.DisplayName.Should().Be("Ana Rivera");
        member.Coverages.Should().ContainSingle().Which.Id.Should().Be("cov-1");
        handler.CapturedUrls.Should().Contain("http://fhir.test/fhir/r4/Coverage?beneficiary=Patient%2FMBR-1001&_count=20");
    }

    [Theory]
    [InlineData("../admin")]
    [InlineData("")]
    public async Task FindMember_never_sends_a_malformed_id(string id)
    {
        var handler = new FakeHandler(HttpStatusCode.OK);

        (await Service(handler).FindMemberAsync(id)).Should().BeNull();
        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task FindMember_unknown_member_is_null()
        => (await Service(new FakeHandler(HttpStatusCode.NotFound)).FindMemberAsync("MBR-9")).Should().BeNull();

    [Fact]
    public async Task GetPackage_sends_the_coverage_and_canonicals_and_reads_the_forms()
    {
        string? sent = null;
        var draft = new QuestionnaireResponse
        {
            Questionnaire = DtrFixtures.Canonical,
            Status = QuestionnaireResponse.QuestionnaireResponseStatus.InProgress,
            Extension = [new Extension("http://hl7.org/fhir/us/davinci-dtr/StructureDefinition/intendedUse", new CodeableConcept("x", "withpa"))],
        };
        var answer = new Parameters
        {
            Parameter =
            [
                new Parameters.ParameterComponent
                {
                    Name = "packagebundle",
                    Resource = new Bundle
                    {
                        Type = Bundle.BundleType.Collection,
                        Entry =
                        [
                            new Bundle.EntryComponent { Resource = DtrFixtures.FormDefinition() },
                            new Bundle.EntryComponent { Resource = DtrFixtures.ReasonsValueSet() },
                            new Bundle.EntryComponent { Resource = draft },
                        ],
                    },
                },
                new Parameters.ParameterComponent
                {
                    Name = "outcome",
                    Resource = new OperationOutcome { Issue = [new OperationOutcome.IssueComponent { Severity = OperationOutcome.IssueSeverity.Information, Code = OperationOutcome.IssueType.Informational, Diagnostics = "Note" }] },
                },
            ],
        };
        var handler = new FakeHandler(r =>
        {
            sent = r.Content!.ReadAsStringAsync().Result;
            return Fhir(HttpStatusCode.OK, answer);
        });

        var package = await Service(handler).GetPackageAsync(DtrFixtures.MemberCoverage(), [DtrFixtures.Canonical], null);

        handler.CapturedUrls.Single().Should().Be("http://fhir.test/fhir/r4/Questionnaire/$questionnaire-package");
        sent.Should().Contain("\"coverage\"").And.Contain(DtrFixtures.Canonical).And.NotContain("\"order\"");
        var form = package.Forms.Should().ContainSingle().Subject;
        form.Questionnaire.Title.Should().Be("Prior authorization: test equipment");
        form.ValueSets.Should().ContainSingle();
        form.ResponseExtensions.Should().ContainSingle().Which.Url.Should().EndWith("intendedUse");
        package.Messages.Should().Equal("Note");
    }

    [Fact]
    public async Task GetPackage_without_a_form_asks_by_service_code()
    {
        string? sent = null;
        var handler = new FakeHandler(r =>
        {
            sent = r.Content!.ReadAsStringAsync().Result;
            return Fhir(HttpStatusCode.OK, new Parameters());
        });

        await Service(handler).GetPackageAsync(DtrFixtures.MemberCoverage(), [], new Coding(DtrFixtures.Hcpcs, "E1390"));

        sent.Should().Contain("\"order\"").And.Contain("ServiceRequest").And.Contain("E1390");
    }

    [Fact]
    public async Task Submit_reads_the_tracking_number_and_decision()
    {
        var claimResponse = new ClaimResponse
        {
            PreAuthRef = "PAS-20261102-ABCD1234",
            Disposition = "Pended for clinical review.",
            Item =
            [
                new ClaimResponse.ItemComponent
                {
                    ItemSequence = 1,
                    Adjudication =
                    [
                        new ClaimResponse.AdjudicationComponent
                        {
                            Category = new CodeableConcept("http://terminology.hl7.org/CodeSystem/adjudication", "submitted"),
                            Extension =
                            [
                                new Extension
                                {
                                    Url = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewAction",
                                    Extension =
                                    [
                                        new Extension("http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-reviewActionCode",
                                            new CodeableConcept("https://codesystem.x12.org/005010/306", "A4")),
                                    ],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
        var handler = new FakeHandler(_ => Fhir(HttpStatusCode.OK, new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = [new Bundle.EntryComponent { Resource = claimResponse }],
        }));

        var outcome = await Service(handler).SubmitPriorAuthAsync(new Bundle());

        outcome.Accepted.Should().BeTrue();
        outcome.AuthorizationNumber.Should().Be("PAS-20261102-ABCD1234");
        outcome.Decision.Should().Be("Pended for review");
        handler.CapturedUrls.Single().Should().Be("http://fhir.test/fhir/r4/Claim/$submit");
    }

    [Fact]
    public async Task Submit_refused_returns_the_operation_outcome_text()
    {
        var handler = new FakeHandler(_ => Fhir((HttpStatusCode)422, new OperationOutcome
        {
            Issue = [new OperationOutcome.IssueComponent { Severity = OperationOutcome.IssueSeverity.Error, Code = OperationOutcome.IssueType.Required, Diagnostics = "Patient.birthDate must be a full date (YYYY-MM-DD)." }],
        }));

        var outcome = await Service(handler).SubmitPriorAuthAsync(new Bundle());

        outcome.Accepted.Should().BeFalse();
        outcome.Problems.Should().Equal("Patient.birthDate must be a full date (YYYY-MM-DD).");
    }

    [Fact]
    public async Task Forbidden_says_which_permission_is_missing_not_the_raw_status()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden);

        var act = () => Service(handler).SaveResponseAsync(new QuestionnaireResponse());

        (await act.Should().ThrowAsync<DtrRequestException>()).Which.Problems.Single().Should().Contain("permission");
    }

    [Fact]
    public async Task A_server_error_is_service_unavailable()
    {
        var act = () => Service(new FakeHandler(HttpStatusCode.BadGateway)).ListQuestionnairesAsync();

        await act.Should().ThrowAsync<ServiceUnavailableException>();
    }

    [Fact]
    public void Response_lines_label_answers_from_the_questionnaire_when_the_response_has_no_text()
    {
        var response = new QuestionnaireResponse
        {
            Item =
            [
                new QuestionnaireResponse.ItemComponent { LinkId = "1", Answer = [new QuestionnaireResponse.AnswerComponent { Value = new Coding(DtrFixtures.Hcpcs, "E0601", "CPAP") }] },
                new QuestionnaireResponse.ItemComponent
                {
                    LinkId = "2",
                    Item = [new QuestionnaireResponse.ItemComponent { LinkId = "2.2", Answer = [new QuestionnaireResponse.AnswerComponent { Value = new Integer(4) }] }],
                },
            ],
        };

        var lines = ResponseLines.From(response, DtrFixtures.FormDefinition());

        lines.Should().Equal(
            new ResponseLine(0, "Equipment", "CPAP"),
            new ResponseLine(0, "Clinical history", null),
            new ResponseLine(1, "Months of symptoms", "4"));
    }
}
