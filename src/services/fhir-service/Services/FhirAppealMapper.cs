using CloudHealthOffice.Appeals.Contracts;
using Hl7.Fhir.Model;
using FhirTask = Hl7.Fhir.Model.Task;

namespace FhirService.Services;

/// <summary>
/// Maps <see cref="AppealDto"/> (appeals-service's wire contract) onto
/// the four FHIR R4 projections: Task, Communication, DocumentReference,
/// ClaimResponse.
///
/// The mapper is intentionally stateless and synchronous — the HTTP
/// seam lives in <see cref="IFhirAppealAdapter"/>, and the FHIR-semantics
/// seam lives here. All mapping is deterministic; drift in the DTO
/// shape is caught by the structural test in
/// <c>CloudHealthOffice.Appeals.Contracts.Tests</c>.
/// </summary>
public sealed class FhirAppealMapper
{
    // ── Canonical URLs ───────────────────────────────────────────────────
    public const string TaskProfileUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-task";
    public const string CommunicationProfileUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-communication";
    public const string DocumentReferenceProfileUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-document-reference";
    public const string ClaimResponseProfileUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-claim-response";

    public const string AppealLevelExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-level";
    public const string AppealLineOfBusinessExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-line-of-business";
    public const string AppealTargetResponseDateExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-target-response-date";
    public const string AppealUrgentFlagExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-urgent-flag";
    public const string AppealTaskReferenceExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-task-reference";
    public const string AppealX12ControlNumberExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-x12-275-control-number";
    public const string AppealX12TransmissionCodeExtensionUrl =
        "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-x12-275-transmission-code";

    public const string AppealClosureReasonCodeSystem =
        "http://fhir.cloudhealthoffice.com/CodeSystem/cho-appeal-closure-reason";

    // ── Task (core appeal work item) ────────────────────────────────────

    public FhirTask ToAppealTask(AppealDto appeal)
    {
        var task = new FhirTask
        {
            Id = appeal.Id,
            Meta = new Meta
            {
                LastUpdated = appeal.UpdatedAt ?? appeal.CreatedAt,
                Profile = [TaskProfileUrl]
            },
            Status = MapTaskStatus(appeal),
            Intent = FhirTask.TaskIntent.Order,
            Code = new CodeableConcept(
                "http://fhir.cloudhealthoffice.com/CodeSystem/cho-appeal-type",
                appeal.AppealType.ToString()),
            Description = $"Appeal {appeal.AppealNumber} for Claim {appeal.ClaimNumber}",
            AuthoredOn = appeal.SubmittedDate.ToString("o"),
            // Task.for → Patient (the member)
            For = new ResourceReference($"Patient/{appeal.MemberId}"),
            // Task.focus → the original denied Claim (post-PR1 correction).
            // The appeal-outcome ClaimResponse, when it exists, links back
            // to this Task via the cho-appeal-task-reference extension.
            Focus = new ResourceReference($"Claim/{appeal.ClaimId}"),
            // Task.requester → the submitting provider (NPI).
            Requester = new ResourceReference($"Practitioner/{appeal.ProviderNPI}")
        };

        if (!string.IsNullOrEmpty(appeal.AssignedReviewerId))
        {
            task.Owner = new ResourceReference($"Practitioner/{appeal.AssignedReviewerId}");
        }

        // businessStatus carries the ClosureReasonCode for observability —
        // clients that want to distinguish Approved / Denied /
        // PartialApproval / Withdrawn on a closed Task read it here
        // rather than re-deriving from the Task.status narrowing.
        if (appeal.ClosureReasonCode.HasValue)
        {
            task.BusinessStatus = new CodeableConcept(
                AppealClosureReasonCodeSystem,
                appeal.ClosureReasonCode.Value.ToString());
        }

        // Extensions: appealLevel, lineOfBusiness, targetResponseDate, urgentFlag.
        task.Extension =
        [
            new Extension
            {
                Url = AppealLevelExtensionUrl,
                Value = new Code(appeal.AppealLevel.ToString())
            },
            new Extension
            {
                Url = AppealLineOfBusinessExtensionUrl,
                Value = new Code(appeal.LineOfBusiness.ToString())
            }
        ];

        if (appeal.TargetResponseDate.HasValue)
        {
            task.Extension.Add(new Extension
            {
                Url = AppealTargetResponseDateExtensionUrl,
                Value = new FhirDateTime(new DateTimeOffset(appeal.TargetResponseDate.Value, TimeSpan.Zero))
            });
        }

        if (appeal.IsUrgent)
        {
            task.Extension.Add(new Extension
            {
                Url = AppealUrgentFlagExtensionUrl,
                Value = new FhirBoolean(true)
            });
        }

        return task;
    }

    /// <summary>
    /// Map AppealStatus + ClosureReasonCode to Task.status. Closed appeals
    /// disambiguate by <see cref="AppealClosureReasonCode"/> — every case
    /// is explicit, no "Completed otherwise" fallback.
    /// </summary>
    internal static FhirTask.TaskStatus MapTaskStatus(AppealDto appeal) =>
        appeal.Status switch
        {
            AppealStatus.Draft => FhirTask.TaskStatus.Draft,
            AppealStatus.Submitted => FhirTask.TaskStatus.Requested,
            AppealStatus.InReview => FhirTask.TaskStatus.InProgress,
            AppealStatus.PendingInfo => FhirTask.TaskStatus.OnHold,
            AppealStatus.Closed => MapClosedTaskStatus(appeal.ClosureReasonCode),
            _ => throw new ArgumentOutOfRangeException(
                nameof(appeal), appeal.Status,
                $"Unhandled AppealStatus '{appeal.Status}' — extend the mapping table.")
        };

    /// <summary>
    /// Explicit per-ClosureReasonCode table for Closed appeals. Every
    /// enum value must be covered. A new ClosureReasonCode requires an
    /// explicit decision here — the default branch throws so that
    /// adding an enum value without updating the mapper fails loudly.
    /// </summary>
    internal static FhirTask.TaskStatus MapClosedTaskStatus(AppealClosureReasonCode? reason) =>
        reason switch
        {
            AppealClosureReasonCode.Approved => FhirTask.TaskStatus.Completed,
            AppealClosureReasonCode.PartialApproval => FhirTask.TaskStatus.Completed,
            AppealClosureReasonCode.Denied => FhirTask.TaskStatus.Rejected,
            AppealClosureReasonCode.Withdrawn => FhirTask.TaskStatus.Cancelled,
            AppealClosureReasonCode.Expired => FhirTask.TaskStatus.Cancelled,
            AppealClosureReasonCode.AdminError => FhirTask.TaskStatus.Cancelled,
            AppealClosureReasonCode.Other => FhirTask.TaskStatus.Cancelled,
            null => throw new InvalidOperationException(
                "A Closed appeal must carry a ClosureReasonCode."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason), reason,
                $"Unhandled AppealClosureReasonCode '{reason}' — extend the mapping table.")
        };

    // ── Communications (one per note) ───────────────────────────────────

    public IEnumerable<Communication> ToAppealCommunications(AppealDto appeal)
    {
        foreach (var note in appeal.Notes)
        {
            yield return ToAppealCommunication(note, appeal.Id, appeal.MemberId);
        }
    }

    /// <summary>
    /// Maps a single <see cref="AppealNoteDto"/> to a FHIR <see cref="Communication"/>.
    /// Used by <c>CommunicationController.Read</c> after a direct GetNoteByIdAsync call.
    /// </summary>
    public Communication ToAppealCommunication(AppealNoteDto note, string appealId, string memberId)
    {
        return new Communication
        {
            Id = note.NoteId,
            Meta = new Meta
            {
                LastUpdated = note.CreatedAt,
                Profile = [CommunicationProfileUrl]
            },
            Status = EventStatus.Completed,
            Subject = new ResourceReference($"Patient/{memberId}"),
            About = [new ResourceReference($"Task/{appealId}")],
            Sent = note.CreatedAt.ToString("o"),
            Sender = new ResourceReference($"Practitioner/{note.CreatedBy}"),
            Payload =
            [
                new Communication.PayloadComponent
                {
                    Content = new FhirString(note.NoteText)
                }
            ],
            Category =
            [
                new CodeableConcept(
                    "http://fhir.cloudhealthoffice.com/CodeSystem/cho-appeal-note-category",
                    note.IsInternal ? "internal" : "external")
            ]
        };
    }

    // ── DocumentReferences (one per attachment + one per clinical doc) ──

    /// <summary>
    /// Maps a single <see cref="AppealAttachmentDto"/> to a FHIR <see cref="DocumentReference"/>.
    /// Used by <c>DocumentReferenceController.Read</c> after a direct GetAttachmentByIdAsync call.
    /// </summary>
    public DocumentReference ToAppealDocumentReference(AppealAttachmentDto att, string appealId, string memberId)
    {
        var docRef = new DocumentReference
        {
            Id = att.AttachmentId,
            Meta = new Meta
            {
                LastUpdated = att.UploadedAt,
                Profile = [DocumentReferenceProfileUrl]
            },
            Status = DocumentReferenceStatus.Current,
            Subject = new ResourceReference($"Patient/{memberId}"),
            Date = att.UploadedAt,
            Type = new CodeableConcept(
                "http://fhir.cloudhealthoffice.com/CodeSystem/cho-275-attachment-type",
                att.AttachmentTypeCode,
                att.AttachmentTypeDescription),
            Content =
            [
                new DocumentReference.ContentComponent
                {
                    Attachment = new Attachment
                    {
                        ContentType = att.ContentType,
                        Url = att.BlobUrl,
                        Title = att.FileName,
                        Size = att.FileSizeBytes.HasValue
                            ? (int?)Math.Min(att.FileSizeBytes.Value, int.MaxValue)
                            : null
                    }
                }
            ],
            Context = new DocumentReference.ContextComponent
            {
                Related = [new ResourceReference($"Task/{appealId}")]
            },
            Extension =
            [
                new Extension
                {
                    Url = AppealX12TransmissionCodeExtensionUrl,
                    Value = new Code(att.TransmissionCode)
                }
            ]
        };

        if (!string.IsNullOrEmpty(att.ControlNumber))
        {
            docRef.Extension.Add(new Extension
            {
                Url = AppealX12ControlNumberExtensionUrl,
                Value = new FhirString(att.ControlNumber)
            });
        }

        if (!string.IsNullOrEmpty(att.Description))
        {
            docRef.Description = att.Description;
        }

        return docRef;
    }

    public IEnumerable<DocumentReference> ToAppealDocumentReferences(AppealDto appeal)
    {
        foreach (var att in appeal.Attachments)
        {
            yield return ToAppealDocumentReference(att, appeal.Id, appeal.MemberId);
        }

        foreach (var doc in appeal.ClinicalDocuments)
        {
            yield return new DocumentReference
            {
                Id = doc.DocumentId,
                Meta = new Meta
                {
                    LastUpdated = appeal.UpdatedAt ?? appeal.CreatedAt,
                    Profile = [DocumentReferenceProfileUrl]
                },
                Status = DocumentReferenceStatus.Current,
                Subject = new ResourceReference($"Patient/{appeal.MemberId}"),
                Type = new CodeableConcept(
                    "http://fhir.cloudhealthoffice.com/CodeSystem/cho-275-attachment-type",
                    doc.DocumentType),
                Description = doc.Summary,
                Content =
                [
                    new DocumentReference.ContentComponent
                    {
                        Attachment = new Attachment { Url = doc.BlobUrl }
                    }
                ],
                Context = new DocumentReference.ContextComponent
                {
                    Related = [new ResourceReference($"Task/{appeal.Id}")]
                }
            };
        }
    }

    // ── ClaimResponse (only for closed appeals with a decision) ─────────

    public ClaimResponse? ToAppealClaimResponse(AppealDto appeal)
    {
        if (appeal.Status != AppealStatus.Closed || appeal.Decision is null)
        {
            return null;
        }

        var decision = appeal.Decision;
        var response = new ClaimResponse
        {
            Id = $"{appeal.Id}-response",
            Meta = new Meta
            {
                LastUpdated = appeal.ClosedAt ?? decision.DecisionDate,
                Profile = [ClaimResponseProfileUrl]
            },
            Status = FinancialResourceStatusCodes.Active,
            Type = new CodeableConcept(
                "http://terminology.hl7.org/CodeSystem/claim-type", "professional"),
            Use = ClaimUseCode.Claim,
            Patient = new ResourceReference($"Patient/{appeal.MemberId}"),
            Created = (appeal.ClosedAt ?? decision.DecisionDate).ToString("o"),
            Insurer = new ResourceReference($"Organization/tenant-{appeal.TenantId}"),
            Request = new ResourceReference($"Claim/{appeal.ClaimId}"),
            Outcome = ClaimProcessingCodes.Complete,
            Disposition = MapDisposition(appeal.ClosureReasonCode),
            // Back-reference to the Task via the PR 1 extension.
            Extension =
            [
                new Extension
                {
                    Url = AppealTaskReferenceExtensionUrl,
                    Value = new ResourceReference($"Task/{appeal.Id}")
                }
            ]
        };

        if (decision.ApprovedAmount.HasValue)
        {
            response.Total =
            [
                new ClaimResponse.TotalComponent
                {
                    Category = new CodeableConcept(
                        "http://terminology.hl7.org/CodeSystem/adjudication",
                        "benefit"),
                    Amount = new Money
                    {
                        Value = decision.ApprovedAmount.Value,
                        Currency = Money.Currencies.USD
                    }
                }
            ];
        }

        return response;
    }

    private static string MapDisposition(AppealClosureReasonCode? reason) =>
        reason switch
        {
            AppealClosureReasonCode.Approved => "Appeal approved.",
            AppealClosureReasonCode.PartialApproval => "Appeal partially approved.",
            AppealClosureReasonCode.Denied => "Appeal denied.",
            AppealClosureReasonCode.Withdrawn => "Appeal withdrawn.",
            AppealClosureReasonCode.Expired => "Appeal closed — response deadline passed.",
            AppealClosureReasonCode.AdminError => "Appeal closed — administrative error.",
            AppealClosureReasonCode.Other => "Appeal closed.",
            null => "Appeal closed.",
            _ => "Appeal closed."
        };
}
