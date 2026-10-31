using DaVinciInterop.Tests.Harness;
using FhirService.Services.Dtr;
using FluentAssertions;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;

namespace DaVinciInterop.Tests.Unit;

/// <summary>
/// CHO's own <c>$questionnaire-package</c> output, judged by the same package
/// view BR-DTR-001 uses to judge the HL7 reference payer. Built independently of
/// the server code, it checks what a DTR client relies on: the package bundle is
/// where the IG puts it, every questionnaire has a canonical and status, no
/// canonical is defined twice, and every dependency the questionnaire names is
/// inside the package. It round-trips through FHIR JSON, as a client receives it.
/// </summary>
[Trait("Category", "DaVinciInteropUnit")]
public sealed class ChoDtrPackageSelfCheckTests
{
    [Fact]
    public void Chos_mri_package_passes_the_reference_payer_package_checks()
    {
        var mri = DtrContentLibrary.Default.Questionnaires.Single(q => q.Id == "q-imaging-mri");
        var coverage = new Coverage { Id = "cov-1", Beneficiary = new ResourceReference("Patient/pat-1") };
        var order = new ServiceRequest
        {
            Id = "sr-1",
            Code = new CodeableConcept("http://www.ama-assn.org/go/cpt", "72148"),
        };
        var issues = new List<OperationOutcome.IssueComponent>();

        var bundles = new QuestionnairePackageBuilder(DtrContentLibrary.Default)
            .Build([new SelectedQuestionnaire(mri, [order])], coverage, DateTimeOffset.UtcNow, null, issues);
        var json = new FhirJsonSerializer().SerializeToString(QuestionnairePackageBuilder.Wrap(bundles, issues));

        var package = DtrQuestionnairePackage.From(new FhirJsonParser().Parse<Parameters>(json));

        package.Should().NotBeNull();
        package!.ProtocolViolations().Should().BeEmpty();
        package.Questionnaire(QuestionnairePackageBuilder.Canonical(mri)).Should().NotBeNull();
        DtrQuestionnairePackage.IsAdaptive(package.Questionnaires.Single()).Should().BeFalse();
        package.QuestionnaireResponses.Should().ContainSingle();
        issues.Should().BeEmpty();
    }
}
