using CloudHealthOffice.Appeals.Contracts;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using FhirTask = Hl7.Fhir.Model.Task;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class FhirAppealMapperTests
{
    private static readonly FhirAppealMapper Mapper = new();

    private static AppealDto NewAppeal(
        AppealStatus status = AppealStatus.InReview,
        AppealClosureReasonCode? reason = null) => new()
    {
        TenantId = "t1",
        Id = "apl-001",
        AppealNumber = "APL-2026-0001",
        ClaimId = "clm-001",
        ClaimNumber = "CLM-001",
        MemberId = "pat-001",
        PatientName = "Test",
        ProviderNPI = "1234567890",
        AppealReason = "Medical necessity",
        AppealType = AppealType.Reconsideration,
        AppealLevel = AppealLevel.FirstLevel,
        LineOfBusiness = LineOfBusiness.Commercial,
        Status = status,
        ClosureReasonCode = reason,
        Source = AppealSource.ProviderPortal,
        SubmittedDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
        IsUrgent = false,
        TargetResponseDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    // ── Task.status mapping table — EXPLICIT PER CLOSURE REASON ────────

    [Theory]
    [InlineData(AppealClosureReasonCode.Approved, FhirTask.TaskStatus.Completed)]
    [InlineData(AppealClosureReasonCode.PartialApproval, FhirTask.TaskStatus.Completed)]
    [InlineData(AppealClosureReasonCode.Denied, FhirTask.TaskStatus.Rejected)]
    [InlineData(AppealClosureReasonCode.Withdrawn, FhirTask.TaskStatus.Cancelled)]
    [InlineData(AppealClosureReasonCode.Expired, FhirTask.TaskStatus.Cancelled)]
    [InlineData(AppealClosureReasonCode.AdminError, FhirTask.TaskStatus.Cancelled)]
    [InlineData(AppealClosureReasonCode.Other, FhirTask.TaskStatus.Cancelled)]
    public void ToAppealTask_status_mapping_covers_every_ClosureReasonCode_when_Closed(
        AppealClosureReasonCode reason, FhirTask.TaskStatus expected)
    {
        var appeal = NewAppeal(AppealStatus.Closed, reason);
        var task = Mapper.ToAppealTask(appeal);
        task.Status.Should().Be(expected);
    }

    [Fact]
    public void Closed_appeal_without_ClosureReasonCode_throws()
    {
        var appeal = NewAppeal(AppealStatus.Closed, reason: null);
        Action act = () => Mapper.ToAppealTask(appeal);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*ClosureReasonCode*");
    }

    [Theory]
    [InlineData(AppealStatus.Draft, FhirTask.TaskStatus.Draft)]
    [InlineData(AppealStatus.Submitted, FhirTask.TaskStatus.Requested)]
    [InlineData(AppealStatus.InReview, FhirTask.TaskStatus.InProgress)]
    [InlineData(AppealStatus.PendingInfo, FhirTask.TaskStatus.OnHold)]
    public void ToAppealTask_maps_non_closed_statuses_directly(
        AppealStatus status, FhirTask.TaskStatus expected)
    {
        var appeal = NewAppeal(status);
        Mapper.ToAppealTask(appeal).Status.Should().Be(expected);
    }

    // ── Closed Task carries the ClosureReasonCode on businessStatus ─────

    [Fact]
    public void Closed_Task_businessStatus_carries_ClosureReasonCode()
    {
        var appeal = NewAppeal(AppealStatus.Closed, AppealClosureReasonCode.PartialApproval);
        var task = Mapper.ToAppealTask(appeal);

        task.BusinessStatus.Should().NotBeNull();
        task.BusinessStatus!.Coding.Should().ContainSingle();
        task.BusinessStatus.Coding[0].System.Should()
            .Be(FhirAppealMapper.AppealClosureReasonCodeSystem);
        task.BusinessStatus.Coding[0].Code.Should().Be("PartialApproval");
    }

    // ── Task.focus points to the original Claim (post-PR1 correction) ───

    [Fact]
    public void ToAppealTask_focus_points_at_original_Claim_not_ClaimResponse()
    {
        var appeal = NewAppeal();
        var task = Mapper.ToAppealTask(appeal);

        task.Focus.Should().NotBeNull();
        task.Focus!.Reference.Should().Be("Claim/clm-001",
            "Task.focus must reference the original denied Claim, not a future ClaimResponse — see PR 1 profile correction.");
    }

    // ── Communications ──────────────────────────────────────────────────

    [Fact]
    public void ToAppealCommunications_one_per_note_with_about_back_reference()
    {
        var appeal = NewAppeal();
        appeal.Notes.Add(new AppealNoteDto
        {
            NoteId = "n1", CreatedBy = "user1", NoteText = "test note", IsInternal = false
        });

        var communications = Mapper.ToAppealCommunications(appeal).ToList();
        communications.Should().ContainSingle();
        communications[0].Id.Should().Be("n1");
        communications[0].About.Should().ContainSingle(r => r.Reference == "Task/apl-001");
    }

    // ── DocumentReferences ──────────────────────────────────────────────

    [Fact]
    public void ToAppealDocumentReferences_275_extensions_populated()
    {
        var appeal = NewAppeal();
        appeal.Attachments.Add(new AppealAttachmentDto
        {
            AttachmentId = "att1",
            AttachmentTypeCode = "OZ",
            TransmissionCode = "EL",
            ControlNumber = "275-001",
            FileName = "op.pdf",
            BlobUrl = "mds://doc-1",
            UploadedAt = DateTime.UtcNow
        });

        var docs = Mapper.ToAppealDocumentReferences(appeal).ToList();
        var att = docs.Single(d => d.Id == "att1");

        att.Extension.Should().Contain(e =>
            e.Url == FhirAppealMapper.AppealX12TransmissionCodeExtensionUrl);
        att.Extension.Should().Contain(e =>
            e.Url == FhirAppealMapper.AppealX12ControlNumberExtensionUrl);
        att.Context!.Related.Should().ContainSingle(r => r.Reference == "Task/apl-001");
    }

    // ── ClaimResponse only for Closed + decision ────────────────────────

    [Fact]
    public void ToAppealClaimResponse_null_for_non_closed()
    {
        Mapper.ToAppealClaimResponse(NewAppeal(AppealStatus.InReview)).Should().BeNull();
    }

    [Fact]
    public void ToAppealClaimResponse_null_for_closed_without_decision()
    {
        var appeal = NewAppeal(AppealStatus.Closed, AppealClosureReasonCode.Withdrawn);
        // no Decision set
        Mapper.ToAppealClaimResponse(appeal).Should().BeNull();
    }

    [Fact]
    public void ToAppealClaimResponse_carries_back_reference_to_Task()
    {
        var appeal = NewAppeal(AppealStatus.Closed, AppealClosureReasonCode.Approved);
        appeal.Decision = new AppealDecisionDto
        {
            DecisionType = AppealDecisionType.Approved,
            ApprovedAmount = 1500m,
            DecisionDate = DateTime.UtcNow
        };
        appeal.ClosedAt = DateTime.UtcNow;

        var response = Mapper.ToAppealClaimResponse(appeal);
        response.Should().NotBeNull();

        response!.Extension.Should().ContainSingle(e =>
            e.Url == FhirAppealMapper.AppealTaskReferenceExtensionUrl);
        var taskRef = response.Extension
            .First(e => e.Url == FhirAppealMapper.AppealTaskReferenceExtensionUrl)
            .Value as ResourceReference;
        taskRef!.Reference.Should().Be("Task/apl-001");
    }
}
