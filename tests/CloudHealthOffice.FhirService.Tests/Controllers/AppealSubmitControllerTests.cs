using CloudHealthOffice.Appeals.Contracts;
using FhirService.Controllers;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class AppealSubmitControllerTests
{
    [Fact]
    public void BuildOperationOutcome_information_issues_for_success_and_specific_codes_for_failure()
    {
        var outcomes = new[]
        {
            new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Appeal,
                ChildRef = "appeal-1",
                Success = true,
                AssignedId = "apl-001",
                HttpStatus = 201,
                FailureKind = AppealSubmitFailureKind.None
            },
            new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Note,
                ChildRef = "note-1",
                Success = false,
                HttpStatus = 422,
                FailureKind = AppealSubmitFailureKind.Processing,
                RetryUrl = "api/appeals/apl-001/notes",
                Diagnostics = "HTTP 422"
            },
            new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Attachment,
                ChildRef = "att-1",
                Success = false,
                HttpStatus = 503,
                FailureKind = AppealSubmitFailureKind.Transient,
                RetryUrl = "api/appeals/apl-001/attachments",
                Diagnostics = "HTTP 503"
            }
        };

        var outcome = AppealSubmitController.BuildOperationOutcome(outcomes, "corr-1");

        outcome.Issue.Should().HaveCount(3);

        outcome.Issue[0].Severity.Should().Be(OperationOutcome.IssueSeverity.Information);
        outcome.Issue[0].Code.Should().Be(OperationOutcome.IssueType.Informational);

        outcome.Issue[1].Severity.Should().Be(OperationOutcome.IssueSeverity.Error);
        outcome.Issue[1].Code.Should().Be(OperationOutcome.IssueType.Processing,
            "4xx downstream rejection → processing (caller may adjust and retry)");

        outcome.Issue[2].Severity.Should().Be(OperationOutcome.IssueSeverity.Error);
        outcome.Issue[2].Code.Should().Be(OperationOutcome.IssueType.Transient,
            "5xx / timeout → transient (retry as-is may succeed)");

        // Retry URL attached to failed issues as an extension.
        outcome.Issue[1].Extension.Should().ContainSingle(e =>
            e.Url == "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-retry-url");
        outcome.Issue[2].Extension.Should().ContainSingle(e =>
            e.Url == "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-retry-url");

        // Correlation id carried on the outcome itself.
        outcome.Extension.Should().ContainSingle(e =>
            e.Url == "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-correlation-id");
    }

    [Fact]
    public void BuildSubmitBundle_rejects_bundle_without_Task_entry()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry =
            [
                new Bundle.EntryComponent { Resource = new Communication() }
            ]
        };

        Action act = () => AppealSubmitController.BuildSubmitBundle(bundle);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Task*");
    }

    [Fact]
    public void BuildSubmitBundle_rejects_bundle_with_multiple_Task_entries()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry =
            [
                new Bundle.EntryComponent { Resource = BuildValidTask() },
                new Bundle.EntryComponent { Resource = BuildValidTask() }
            ]
        };

        Action act = () => AppealSubmitController.BuildSubmitBundle(bundle);
        act.Should().Throw<InvalidOperationException>().WithMessage("*exactly one Task*");
    }

    [Fact]
    public void BuildSubmitBundle_accepts_Task_plus_notes_plus_attachments()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry =
            [
                new Bundle.EntryComponent { Resource = BuildValidTask() },
                new Bundle.EntryComponent { Resource = BuildValidPatient() },
                new Bundle.EntryComponent
                {
                    Resource = new Communication
                    {
                        Id = "n1",
                        Status = EventStatus.Completed,
                        Payload = [new Communication.PayloadComponent { Content = new FhirString("hello") }]
                    }
                },
                new Bundle.EntryComponent
                {
                    Resource = new DocumentReference
                    {
                        Id = "att1",
                        Status = DocumentReferenceStatus.Current,
                        Type = new CodeableConcept(null, "OZ"),
                        Content = [new DocumentReference.ContentComponent
                        {
                            Attachment = new Attachment { Url = "mds://x" }
                        }]
                    }
                }
            ]
        };

        var dto = AppealSubmitController.BuildSubmitBundle(bundle);
        dto.Appeal.ClaimId.Should().Be("c1");
        dto.Notes.Should().ContainSingle();
        dto.Notes[0].NoteText.Should().Be("hello");
        dto.Attachments.Should().ContainSingle();
    }

    [Fact]
    public void BuildSubmitBundle_rejects_non_transaction_bundle_type()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry = [new Bundle.EntryComponent { Resource = BuildValidTask() }]
        };

        Action act = () => AppealSubmitController.BuildSubmitBundle(bundle);
        act.Should().Throw<InvalidOperationException>().WithMessage("*transaction*");
    }

    [Fact]
    public void BuildSubmitBundle_rejects_bundle_without_Patient_entry()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry = [new Bundle.EntryComponent { Resource = BuildValidTask() }]
        };

        Action act = () => AppealSubmitController.BuildSubmitBundle(bundle);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Patient*");
    }

    [Fact]
    public void BuildSubmitBundle_assigns_correct_entry_indices()
    {
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Transaction,
            Entry =
            [
                // index 0: Patient
                new Bundle.EntryComponent { Resource = BuildValidPatient() },
                // index 1: Task
                new Bundle.EntryComponent { Resource = BuildValidTask() },
                // index 2: Communication
                new Bundle.EntryComponent
                {
                    Resource = new Communication
                    {
                        Id = "n1",
                        Status = EventStatus.Completed,
                        Payload = [new Communication.PayloadComponent { Content = new FhirString("hi") }]
                    }
                }
            ]
        };

        var dto = AppealSubmitController.BuildSubmitBundle(bundle);

        dto.AppealEntryIndex.Should().Be(1, "Task is at index 1");
        dto.NoteEntryIndices.Should().ContainSingle()
            .Which.Should().Be(2, "Communication is at index 2");
    }

    [Fact]
    public void BuildOperationOutcome_uses_FHIRPath_location_and_cho_child_ref_extension()
    {
        var outcomes = new[]
        {
            new AppealSubmitChildOutcome
            {
                Kind = AppealSubmitChildKind.Appeal,
                ChildRef = "apl-new",
                EntryIndex = 1,
                Success = true,
                AssignedId = "apl-001",
                HttpStatus = 201,
                FailureKind = AppealSubmitFailureKind.None
            }
        };

        var outcome = AppealSubmitController.BuildOperationOutcome(outcomes, "corr-x");

        outcome.Issue[0].Location.Should().ContainSingle()
            .Which.Should().Be("Bundle.entry[1].resource",
                "FHIRPath location must use EntryIndex, not ChildRef");

        outcome.Issue[0].Extension.Should().ContainSingle(e =>
            e.Url == "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-child-ref" &&
            (e.Value as FhirString)!.Value == "apl-new",
            "cho-appeal-child-ref extension must carry the ChildRef value");
    }

    // ── Coded-value mapping (regulatory clock selection) ────────────────

    [Fact]
    public void TaskToAppealDto_defaults_when_codes_absent()
    {
        var task = BuildValidTask();
        task.Code = null;

        var dto = AppealSubmitController.TaskToAppealDto(task, BuildValidPatient(), claim: null);

        dto.AppealType.Should().Be(AppealType.Reconsideration);
        dto.AppealLevel.Should().Be(AppealLevel.FirstLevel);
        dto.LineOfBusiness.Should().Be(LineOfBusiness.Commercial);
    }

    [Theory]
    [InlineData("ExternalReview")]
    [InlineData("external-review")]
    [InlineData("EXTERNAL_REVIEW")]
    [InlineData("ire")]
    [InlineData("state-fair-hearing")]
    public void ExternalReview_Task_code_without_level_extension_lands_on_external_review_level(string code)
    {
        var task = BuildValidTask();
        task.Code = new CodeableConcept(null, code);

        var (type, level) = AppealSubmitController.ResolveTypeAndLevel(task);

        type.Should().Be(AppealType.ExternalReview);
        level.Should().Be(AppealLevel.ExternalReview,
            "without the level the appeal would be held to the internal-appeal clock and a legitimate external-review date rejected");
    }

    [Fact]
    public void ExternalReview_level_extension_without_Task_code_lands_on_external_review_type()
    {
        var task = BuildValidTask();
        task.Code = null;
        task.Extension.Add(new Extension(FhirAppealMapper.AppealLevelExtensionUrl, new Code("external-review")));

        var (type, level) = AppealSubmitController.ResolveTypeAndLevel(task);

        type.Should().Be(AppealType.ExternalReview);
        level.Should().Be(AppealLevel.ExternalReview);
    }

    [Fact]
    public void Explicit_type_and_level_are_not_rewritten()
    {
        var task = BuildValidTask();
        task.Code = new CodeableConcept(null, "Reconsideration");
        task.Extension.Add(new Extension(FhirAppealMapper.AppealLevelExtensionUrl, new Code("ExternalReview")));

        var (type, level) = AppealSubmitController.ResolveTypeAndLevel(task);

        type.Should().Be(AppealType.Reconsideration);
        level.Should().Be(AppealLevel.ExternalReview);
    }

    [Fact]
    public void Grievance_is_never_promoted_to_external_review()
    {
        var task = BuildValidTask();
        task.Code = new CodeableConcept(null, "Grievance");

        var (type, level) = AppealSubmitController.ResolveTypeAndLevel(task);

        type.Should().Be(AppealType.Grievance);
        level.Should().Be(AppealLevel.FirstLevel);
    }

    [Theory]
    [InlineData("MedicarePartD", LineOfBusiness.MedicarePartD)]
    [InlineData("medicare-part-d", LineOfBusiness.MedicarePartD)]
    [InlineData("PartD", LineOfBusiness.MedicarePartD)]
    [InlineData("Medicare", LineOfBusiness.Medicare)]
    [InlineData("medicare-advantage", LineOfBusiness.Medicare)]
    [InlineData("Medicaid", LineOfBusiness.Medicaid)]
    [InlineData("exchange", LineOfBusiness.Marketplace)]
    public void LineOfBusiness_extension_maps_including_PartD(string code, LineOfBusiness expected)
    {
        var task = BuildValidTask();
        task.Extension.Add(new Extension(FhirAppealMapper.AppealLineOfBusinessExtensionUrl, new Code(code)));

        AppealSubmitController.TaskToAppealDto(task, BuildValidPatient(), claim: null)
            .LineOfBusiness.Should().Be(expected);
    }

    [Theory]
    [InlineData(FhirAppealMapper.AppealLineOfBusinessExtensionUrl, "Tricare")]
    [InlineData(FhirAppealMapper.AppealLineOfBusinessExtensionUrl, "5")]
    [InlineData(FhirAppealMapper.AppealLevelExtensionUrl, "ThirdLevel")]
    public void Unrecognized_coded_extension_is_rejected_not_defaulted(string url, string code)
    {
        var task = BuildValidTask();
        task.Extension.Add(new Extension(url, new Code(code)));

        Action act = () => AppealSubmitController.TaskToAppealDto(task, BuildValidPatient(), claim: null);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*'{code}'*");
    }

    [Fact]
    public void Unrecognized_Task_code_is_rejected_not_defaulted()
    {
        var task = BuildValidTask();
        task.Code = new CodeableConcept(null, "Arbitration");

        Action act = () => AppealSubmitController.TaskToAppealDto(task, BuildValidPatient(), claim: null);

        act.Should().Throw<InvalidOperationException>().WithMessage("*AppealType*Arbitration*");
    }

    private static Hl7.Fhir.Model.Task BuildValidTask() => new()
    {
        Id = "apl-new",
        Status = Hl7.Fhir.Model.Task.TaskStatus.Draft,
        Intent = Hl7.Fhir.Model.Task.TaskIntent.Order,
        For = new ResourceReference("Patient/p1"),
        Focus = new ResourceReference("Claim/c1"),
        Requester = new ResourceReference("Practitioner/prov-1"),
        Code = new CodeableConcept(null, "Reconsideration")
    };

    private static Patient BuildValidPatient() => new()
    {
        Id = "p1",
        Name = [new HumanName { Family = "Doe", Given = ["Jane"] }]
    };
}
