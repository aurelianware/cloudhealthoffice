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
