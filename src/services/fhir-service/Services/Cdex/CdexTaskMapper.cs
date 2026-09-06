using Hl7.Fhir.Model;
using FhirTask = Hl7.Fhir.Model.Task;

namespace FhirService.Services.Cdex;

/// <summary>
/// Projects an additional-information request onto a FHIR <c>Task</c> on the
/// Da Vinci CDex Task Attachment Request profile.
///
/// A PROJECTION, not a second record: every element below is read from the one
/// <c>RfaiCase</c> rfai-service holds. Nothing here is stored, so the Task a
/// provider reads is always the current state of the request, and there is no
/// way for the standards-facing representation and the internal case to
/// disagree.
///
/// STATUS MAPPING. FHIR's <c>Task.status</c> vocabulary is narrower than CHO's
/// four RFAI states, so the CHO state is ALSO carried on
/// <c>Task.businessStatus</c> — nothing is lost in translation:
///
/// <code>
/// RFAI state    Task.status  businessStatus  meaning
/// Open          requested    Open            payer is waiting on the provider
/// DocsReceived  completed    DocsReceived    a response was accepted; PA back in review
/// Closed (answered)  completed  Closed       cycle finished with a response
/// Closed (unanswered) failed   Closed        cycle ended without the information
/// Cancelled     cancelled    Cancelled       request withdrawn
/// </code>
///
/// CONTENT. The Task carries the request and the FACT of each response — codes,
/// control numbers, hashes, sizes. It never carries the submitted content, a
/// storage URL, or a resolvable read of the artifact: the provider already holds
/// what they sent, and the payer's copy is not re-exposed over this surface.
/// </summary>
public sealed class CdexTaskMapper
{
    /// <summary>
    /// Prefix on every projected Task id. It is the case's own document id
    /// prefix, so <c>GET Task/{id}</c> can tell an additional-information
    /// request from an appeal Task without a lookup against both stores.
    /// </summary>
    public const string TaskIdPrefix = "rfai-";

    public static bool IsAdditionalInformationTaskId(string? id)
        => !string.IsNullOrEmpty(id)
           && id.StartsWith(TaskIdPrefix, StringComparison.Ordinal);

    public FhirTask ToAttachmentRequestTask(CdexAdditionalInformationRequest request)
    {
        var (status, businessStatus) = MapStatus(request);

        var task = new FhirTask
        {
            Id = request.Id,
            Meta = new Meta
            {
                Profile = [CdexCanonicalUrls.TaskAttachmentRequestProfile],
                LastUpdated = ToOffset(request.UpdatedAt),
            },
            Status = status,
            Intent = FhirTask.TaskIntent.Order,
            AuthoredOn = ToFhirInstant(request.CreatedAt),
            LastModified = ToFhirInstant(request.UpdatedAt),

            // Task.code says WHAT KIND of task this is: a payer asking for
            // attachments. Without it a consumer cannot tell this Task from any
            // other Task on the same endpoint.
            Code = new CodeableConcept(
                CdexCanonicalUrls.TempCodeSystem,
                CdexCanonicalUrls.AttachmentRequestCode,
                "Request for Attachments"),

            BusinessStatus = new CodeableConcept(
                CdexCanonicalUrls.RfaiStatusCodeSystem,
                businessStatus,
                businessStatus),

            // The payer asking. The provider expected to answer owns the task.
            Requester = new ResourceReference { Display = "CHO Payer" },
        };

        // The provider-facing handle a submission quotes. FIRST identifier:
        // it is the one a caller searches on.
        task.Identifier.Add(new Identifier
        {
            System = CdexCanonicalUrls.TrackingIdSystem,
            Value = request.TrackingId,
            Use = Identifier.IdentifierUse.Official,
        });

        // The authorization this request belongs to, on the Task itself as well
        // as on focus — so the correlation survives even a consumer that ignores
        // references.
        task.Identifier.Add(new Identifier
        {
            System = CdexCanonicalUrls.AuthorizationNumberSystem,
            Value = request.AuthNumber,
            Use = Identifier.IdentifierUse.Secondary,
        });

        // Task.focus — the prior authorization. A LOGICAL reference: the
        // authorization is a PAS Claim (use = preauthorization) identified by the
        // preAuthRef the submitter already holds. The literal reference is given
        // so `Task?focus=Claim/{authNumber}` resolves the search; the identifier
        // is what carries the authority.
        task.Focus = new ResourceReference
        {
            Reference = $"Claim/{request.AuthNumber}",
            Type = "Claim",
            Identifier = new Identifier
            {
                System = CdexCanonicalUrls.AuthorizationNumberSystem,
                Value = request.AuthNumber,
            },
            Display = $"Prior authorization {request.AuthNumber}",
        };

        if (!string.IsNullOrWhiteSpace(request.MemberId))
        {
            task.For = new ResourceReference(
                $"Patient/{StripPatientPrefix(request.MemberId)}");
        }

        if (!string.IsNullOrWhiteSpace(request.RequestingProviderNpi))
        {
            // Identifier-only: the NPI is the authority, and this server does not
            // serve an Organization read for it.
            task.Owner = new ResourceReference
            {
                Type = "Organization",
                Identifier = new Identifier(
                    CdexCanonicalUrls.UsNpi, request.RequestingProviderNpi),
                Display = $"Requesting provider {request.RequestingProviderNpi}",
            };
        }

        // The due date, as a restriction on the request rather than as prose.
        if (request.DueDate.HasValue)
        {
            task.Restriction = new FhirTask.RestrictionComponent
            {
                Period = new Period { End = ToFhirInstant(request.DueDate.Value) },
            };
        }

        AddReason(task, request);
        AddRequestedItems(task, request);
        AddResponses(task, request);

        // Free text SUPPLEMENTS the coded request; it never stands in for it.
        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            task.Note.Add(new Annotation { Text = new Markdown(request.Notes) });
        }

        return task;
    }

    /// <summary>
    /// Why the payer is asking. The X12 review decision that caused the request
    /// is a coding on the same concept, so a consumer can tie the Task back to
    /// the A4 decision it came from without a second lookup.
    /// </summary>
    private static void AddReason(FhirTask task, CdexAdditionalInformationRequest request)
    {
        var reason = new CodeableConcept();

        if (!string.IsNullOrWhiteSpace(request.ReviewDecision))
        {
            reason.Coding.Add(new Coding(
                CdexCanonicalUrls.X12ReviewDecision,
                request.ReviewDecision,
                "Pended — additional information required"));
        }

        if (!string.IsNullOrWhiteSpace(request.ReasonCode))
        {
            reason.Coding.Add(new Coding(
                CdexCanonicalUrls.TempCodeSystem,
                request.ReasonCode,
                request.ReasonDescription));
        }

        if (!string.IsNullOrWhiteSpace(request.ReasonDescription))
            reason.Text = request.ReasonDescription;

        if (reason.Coding.Count > 0 || reason.Text is not null)
            task.ReasonCode = reason;
    }

    /// <summary>
    /// What is being asked for, as <c>Task.input</c> — one input per requested
    /// item, coded rather than described. The purpose of use is stated once for
    /// the whole request: coverage authorization, which is the only purpose a
    /// prior-authorization documentation request can have.
    /// </summary>
    private static void AddRequestedItems(FhirTask task, CdexAdditionalInformationRequest request)
    {
        task.Input.Add(new FhirTask.ParameterComponent
        {
            Type = new CodeableConcept(
                CdexCanonicalUrls.TempCodeSystem,
                CdexCanonicalUrls.PurposeOfUse,
                "Purpose of Use"),
            Value = new CodeableConcept(
                CdexCanonicalUrls.ActReason,
                CdexCanonicalUrls.CoverageAuthPurposeOfUse,
                "coverage authorization"),
        });

        // CHO does not require a signed response. Stated explicitly rather than
        // left to inference, because a consumer that assumes the wrong default
        // either over-signs or under-signs.
        task.Input.Add(new FhirTask.ParameterComponent
        {
            Type = new CodeableConcept(
                CdexCanonicalUrls.TempCodeSystem,
                CdexCanonicalUrls.SignatureFlag,
                "Signature Required"),
            Value = new FhirBoolean(false),
        });

        foreach (var item in request.RequestedItems)
        {
            var what = new CodeableConcept { Text = item.Description };

            if (!string.IsNullOrWhiteSpace(item.LoincCode))
                what.Coding.Add(new Coding(CdexCanonicalUrls.Loinc, item.LoincCode, item.Description));

            // The X12 PWK code alongside LOINC: the same request has to be
            // expressible on the 277/275 wire as well as on FHIR.
            if (!string.IsNullOrWhiteSpace(item.Code))
            {
                what.Coding.Add(new Coding(
                    CdexCanonicalUrls.X12AttachmentReportType, item.Code, item.Description));
            }

            task.Input.Add(new FhirTask.ParameterComponent
            {
                Type = new CodeableConcept(
                    CdexCanonicalUrls.TempCodeSystem,
                    CdexCanonicalUrls.AttachmentCode,
                    item.Required ? "Attachment Code (required)" : "Attachment Code (optional)"),
                Value = what,
            });

            // The service line the question is about, so a provider knows WHICH
            // part of a multi-line request is short of documentation.
            if (!string.IsNullOrWhiteSpace(item.ServiceLineProcedureCode))
            {
                task.Input.Add(new FhirTask.ParameterComponent
                {
                    Type = new CodeableConcept(
                        CdexCanonicalUrls.TempCodeSystem,
                        CdexCanonicalUrls.LineItem,
                        "Line Item"),
                    Value = new CodeableConcept(
                        CdexCanonicalUrls.Hcpcs, item.ServiceLineProcedureCode, item.Description),
                });
            }

            // The diagnosis the question is about is NOT an attachment code, and
            // must not be typed as one: a consumer reading Task.input by type
            // would otherwise take a diagnosis for a document being requested.
            // CDex defines no input type for per-item diagnosis context, so it is
            // named in CHO's own code system rather than by overloading one of
            // theirs.
            if (!string.IsNullOrWhiteSpace(item.DiagnosisCode))
            {
                task.Input.Add(new FhirTask.ParameterComponent
                {
                    Type = new CodeableConcept(
                        CdexCanonicalUrls.ChoTaskInputCodeSystem,
                        CdexCanonicalUrls.DiagnosisContext,
                        "Diagnosis context"),
                    Value = new CodeableConcept(
                        CdexCanonicalUrls.Icd10Cm, item.DiagnosisCode, item.Description),
                });
            }
        }
    }

    /// <summary>
    /// What has come back, as <c>Task.output</c> — one output per accepted
    /// artifact. The value is an identifier-only DocumentReference: it names the
    /// artifact and its type, and deliberately does NOT resolve. CHO does not
    /// serve the submitted clinical content back over this surface.
    /// </summary>
    private static void AddResponses(FhirTask task, CdexAdditionalInformationRequest request)
    {
        foreach (var artifact in request.ReceivedAttachments)
        {
            var type = new CodeableConcept();

            if (!string.IsNullOrWhiteSpace(artifact.DocumentTypeCode))
            {
                type.Coding.Add(new Coding(
                    artifact.DocumentTypeSystem ?? CdexCanonicalUrls.Loinc,
                    artifact.DocumentTypeCode,
                    artifact.Title));
            }

            if (type.Coding.Count == 0)
            {
                type.Coding.Add(new Coding(
                    CdexCanonicalUrls.TempCodeSystem,
                    CdexCanonicalUrls.AttachmentCode,
                    "Attachment"));
            }

            task.Output.Add(new FhirTask.OutputComponent
            {
                Type = type,
                Value = new ResourceReference
                {
                    Type = "DocumentReference",
                    Identifier = new Identifier(
                        CdexCanonicalUrls.SubmissionIdSystem, artifact.SubmissionId),
                    Display = BuildArtifactDisplay(artifact),
                },
            });
        }
    }

    /// <summary>
    /// Receipt metadata only — content type, size and the received date. Never
    /// the title's clinical content beyond what the submitter chose to call it,
    /// and never the bytes.
    /// </summary>
    private static string BuildArtifactDisplay(CdexReceivedArtifact artifact)
    {
        var contentType = string.IsNullOrWhiteSpace(artifact.ContentType)
            ? "attachment"
            : artifact.ContentType;

        var size = artifact.SizeBytes.HasValue ? $", {artifact.SizeBytes} bytes" : string.Empty;

        return $"Received {contentType}{size} on {artifact.ReceivedAt:yyyy-MM-dd}";
    }

    /// <summary>The status table above, as code. Total over the enum.</summary>
    internal static (FhirTask.TaskStatus Status, string BusinessStatus) MapStatus(
        CdexAdditionalInformationRequest request)
    {
        var answered = request.ReceivedAttachments.Count > 0;

        return request.Status switch
        {
            CdexAdditionalInformationStatus.Open
                => (FhirTask.TaskStatus.Requested, "Open"),
            CdexAdditionalInformationStatus.DocsReceived
                => (FhirTask.TaskStatus.Completed, "DocsReceived"),

            // A cycle closed WITH a response completed; one closed without the
            // information did not. Reporting both as "completed" would tell a
            // provider their unanswered request had been satisfied.
            CdexAdditionalInformationStatus.Closed
                => (answered ? FhirTask.TaskStatus.Completed : FhirTask.TaskStatus.Failed, "Closed"),

            CdexAdditionalInformationStatus.Cancelled
                => (FhirTask.TaskStatus.Cancelled, "Cancelled"),

            // An unrecognised state is never reported as satisfied: it reads as
            // still outstanding rather than as a completion CHO cannot vouch for.
            _ => (FhirTask.TaskStatus.Requested, request.Status.ToString()),
        };
    }

    private static string ToFhirInstant(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static DateTimeOffset ToOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string StripPatientPrefix(string value)
        => value.StartsWith("Patient/", StringComparison.Ordinal)
            ? value["Patient/".Length..]
            : value;
}
