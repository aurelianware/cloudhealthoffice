using FhirService.Services.Identity;
using System.Text.Json;
using CloudHealthOffice.Appeals.Contracts;
using FhirService.Services;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// <c>$cho-appeal-submit</c> FHIR operation — POSTs a FHIR Bundle that
/// carries the appeal Task + associated notes (Communication) +
/// attachments (DocumentReference) and drives the appeals-service
/// REST surface child-by-child. Returns an OperationOutcome that
/// enumerates per-child outcomes with:
/// <list type="bullet">
///   <item><c>processing</c> issue code for downstream 4xx.</item>
///   <item><c>transient</c> issue code for network/timeout/5xx.</item>
///   <item>Redacted diagnostics that preserve structural information and
///     drop anything PHI-adjacent.</item>
///   <item>Retry-URL per failed child so the caller can re-POST just
///     the individual failed entry.</item>
/// </list>
///
/// Atomicity caveat: the top-level appeal create is the gating call;
/// notes and attachments submit serially. A failure on the appeal
/// create short-circuits the rest of the bundle. A failure on a note
/// or attachment leaves the appeal intact and only that child
/// unprocessed — the caller retries it via the returned retry URL.
/// </summary>
[Route("fhir/r4")]
[FhirAccess(smart: true, cho: "appeals:write")]
public sealed class AppealSubmitController : FhirControllerBase
{
    public const string OperationName = "cho-appeal-submit";
    public const string IssueLocationPrefix = "Bundle.entry";

    private readonly IFhirAppealAdapter _appeals;
    private readonly ICorrelationIdAccessor _correlation;
    private readonly ILogger<AppealSubmitController> _logger;

    public AppealSubmitController(
        IFhirAppealAdapter appeals,
        ICorrelationIdAccessor correlation,
        ILogger<AppealSubmitController> logger)
    {
        _appeals = appeals;
        _correlation = correlation;
        _logger = logger;
    }

    /// <summary>
    /// POST /fhir/r4/$cho-appeal-submit
    /// Body: FHIR Bundle (type=transaction) containing one Task entry
    /// plus zero or more Communication and DocumentReference entries.
    /// </summary>
    [HttpPost("$cho-appeal-submit")]
    [Consumes("application/fhir+json", "application/json")]
    [ProducesResponseType(typeof(OperationOutcome), 200)]
    [ProducesResponseType(typeof(OperationOutcome), 400)]
    [ProducesResponseType(typeof(OperationOutcome), 422)]
    public async Task<IActionResult> Submit([FromBody] Bundle bundle, CancellationToken ct)
    {
        // Anchor every sub-call in this submit to one correlation ID.
        // Fresh GUID unless the caller supplied one via X-Correlation-Id
        // (handled by middleware in the future — for now we always
        // overwrite so the submit sequence is coherent).
        var correlationId = $"appeal-submit-{Guid.NewGuid():D}";
        _correlation.Set(correlationId);

        if (bundle is null)
        {
            return BadRequest(BuildOutcome(
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Invalid,
                "Request body must be a FHIR Bundle."));
        }

        AppealSubmitBundleDto dto;
        try
        {
            dto = BuildSubmitBundle(bundle);
        }
        catch (AppealSubmitValueException ex)
        {
            // A recognizable Bundle carrying a coded value we cannot map
            // (unknown line of business / appeal level / type). Defaulting
            // it would put the appeal on the wrong regulatory clock, so the
            // caller gets an OperationOutcome instead.
            return UnprocessableEntity(BuildOutcome(
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.CodeInvalid,
                ex.Message,
                ex.Location));
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(BuildOutcome(
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Structure,
                ex.Message));
        }

        // A patient-bound token appeals for its own member only.
        if (IsOutsidePatientContext(dto.Appeal.MemberId))
        {
            return StatusCode(403, BuildOutcome(
                OperationOutcome.IssueSeverity.Error,
                OperationOutcome.IssueType.Forbidden,
                "A patient-context token cannot submit an appeal for another member."));
        }

        // The actor is the authenticated caller, never Communication.sender
        // or anything else in the submitted Bundle.
        var actor = AuthenticatedActorId ?? "unknown";
        dto.Appeal.SubmittedBy = actor;
        dto.Appeal.CreatedBy = actor;
        foreach (var note in dto.Notes)
            note.CreatedBy = actor;

        var outcomes = await _appeals.SubmitAppealAsync(dto, TenantId, ct);
        var operationOutcome = BuildOperationOutcome(outcomes, correlationId);

        // Partial or total failure still returns 200 with an
        // OperationOutcome carrying the per-child issues — clients walk
        // the issues list and retry individual children. This matches
        // the FHIR convention for operation responses that aggregate
        // results rather than failing atomically.
        return Ok(operationOutcome);
    }

    // ── Bundle → AppealSubmitBundleDto projection ───────────────────────

    internal static AppealSubmitBundleDto BuildSubmitBundle(Bundle bundle)
    {
        // Fix 4: reject non-transaction bundles early
        if (bundle.Type != Bundle.BundleType.Transaction)
        {
            throw new InvalidOperationException(
                $"Bundle.type must be 'transaction' for $cho-appeal-submit; got '{bundle.Type}'.");
        }

        Hl7.Fhir.Model.Task? taskEntry = null;
        Patient? patient = null;
        Claim? claim = null;
        var communications = new List<(Communication comm, int entryIndex)>();
        var documentReferences = new List<(DocumentReference doc, int entryIndex)>();
        var taskEntryIndex = 0;

        var entryIndex = 0;
        foreach (var entry in bundle.Entry ?? [])
        {
            switch (entry.Resource)
            {
                case Hl7.Fhir.Model.Task t when taskEntry is null:
                    taskEntry = t;
                    taskEntryIndex = entryIndex;
                    break;
                case Hl7.Fhir.Model.Task:
                    throw new InvalidOperationException(
                        "Bundle must contain exactly one Task entry.");
                case Patient p:
                    patient = p;
                    break;
                case Claim c:
                    claim = c;
                    break;
                case Communication c:
                    communications.Add((c, entryIndex));
                    break;
                case DocumentReference d:
                    documentReferences.Add((d, entryIndex));
                    break;
                case null:
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported resource type '{entry.Resource.TypeName}' in Bundle; " +
                        "only Task, Patient, Claim, Communication, and DocumentReference are accepted.");
            }
            entryIndex++;
        }

        if (taskEntry is null)
        {
            throw new InvalidOperationException(
                "Bundle must contain a Task entry representing the appeal.");
        }

        // Fix 2: require Patient entry for PatientName mapping
        if (patient is null)
        {
            throw new InvalidOperationException(
                "Bundle must contain a Patient entry; PatientName cannot be populated without it.");
        }

        var appealDto = TaskToAppealDto(taskEntry, patient, claim);
        var noteDtos = communications.Select(t => CommunicationToNoteDto(t.comm)).ToList();
        var attachmentDtos = documentReferences.Select(t => DocumentReferenceToAttachmentDto(t.doc)).ToList();

        return new AppealSubmitBundleDto
        {
            Appeal = appealDto,
            Notes = noteDtos,
            Attachments = attachmentDtos,
            AppealEntryIndex = taskEntryIndex,
            NoteEntryIndices = communications.Select(t => t.entryIndex).ToArray(),
            AttachmentEntryIndices = documentReferences.Select(t => t.entryIndex).ToArray()
        };
    }

    internal static AppealDto TaskToAppealDto(Hl7.Fhir.Model.Task task, Patient patient, Claim? claim)
    {
        // Light conversion — extract the fields appeals-service requires
        // on create. Deep profile validation (all required extensions
        // present, bindings honored) is deferred to a future PR.
        var forRef = task.For?.Reference ?? string.Empty;
        var focusRef = task.Focus?.Reference ?? string.Empty;
        var requesterRef = task.Requester?.Reference ?? string.Empty;

        var memberId = StripPrefix("Patient/", forRef);
        var claimId = StripPrefix("Claim/", focusRef);
        var providerNpi = StripPrefix("Practitioner/", requesterRef);

        if (string.IsNullOrEmpty(memberId))
            throw new InvalidOperationException("Task.for (Patient reference) is required.");
        if (string.IsNullOrEmpty(claimId))
            throw new InvalidOperationException("Task.focus (Claim reference) is required.");
        if (string.IsNullOrEmpty(providerNpi))
            throw new InvalidOperationException("Task.requester (Practitioner reference) is required.");

        // Fix 2: Extract PatientName from Patient resource
        var nameComponent = patient.Name.FirstOrDefault();
        var patientName = nameComponent?.Text
            ?? string.Join(" ",
                new[] { nameComponent?.Family, nameComponent?.Given.FirstOrDefault() }
                    .Where(s => !string.IsNullOrEmpty(s)));

        // Fix 2: Extract ClaimNumber from Claim.identifier or fall back to claimId
        var claimNumber = claim?.Identifier.FirstOrDefault()?.Value ?? claimId!;

        // TODO(appeals-followup-fhir-validation): deep profile-level
        // validation (Task.extension:appealLevel / lineOfBusiness /
        // targetResponseDate / urgentFlag presence, Task.code binding)
        // lands in a future PR.

        var (appealType, appealLevel) = ResolveTypeAndLevel(task);

        return new AppealDto
        {
            Id = task.Id ?? string.Empty,
            MemberId = memberId!,
            ClaimId = claimId!,
            ProviderNPI = providerNpi!,
            AppealType = appealType,
            AppealLevel = appealLevel,
            LineOfBusiness = ParseCodedExtension(task,
                FhirAppealMapper.AppealLineOfBusinessExtensionUrl,
                LineOfBusiness.Commercial, LineOfBusinessAliases),
            Status = AppealStatus.Draft,
            Source = AppealSource.ProviderPortal,
            AppealReason = task.Description ?? string.Empty,
            AppealNumber = string.Empty,
            ClaimNumber = claimNumber,
            PatientName = patientName ?? string.Empty,
            IsUrgent = HasExtensionTrue(task, FhirAppealMapper.AppealUrgentFlagExtensionUrl),
            TargetResponseDate = ReadDateTimeExtension(task,
                FhirAppealMapper.AppealTargetResponseDateExtensionUrl),
            AssignedReviewerId = StripPrefix("Practitioner/", task.Owner?.Reference)
        };
    }

    // ── Coded-value mapping ─────────────────────────────────────────────
    //
    // Task.code and the appealLevel / lineOfBusiness extensions drive the
    // regulatory response clock in appeals-service
    // (AppealResponseDeadlinePolicy). A value that silently fell back to
    // the default (Reconsideration / FirstLevel / Commercial) would put an
    // external review, State Fair Hearing or Part D appeal on the internal
    // commercial clock and get its legitimate TargetResponseDate rejected.
    // So: absent → default; present but unrecognized → 422.
    //
    // Matching is case-insensitive and ignores '-', '_' and spaces, so
    // "ExternalReview", "external-review" and "EXTERNAL_REVIEW" all map.

    private static readonly IReadOnlyDictionary<string, AppealType> AppealTypeAliases =
        new Dictionary<string, AppealType>(StringComparer.OrdinalIgnoreCase)
        {
            ["ire"] = AppealType.ExternalReview,
            ["independentreview"] = AppealType.ExternalReview,
            ["statefairhearing"] = AppealType.ExternalReview,
            ["fairhearing"] = AppealType.ExternalReview,
            ["redetermination"] = AppealType.Reconsideration
        };

    private static readonly IReadOnlyDictionary<string, AppealLevel> AppealLevelAliases =
        new Dictionary<string, AppealLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = AppealLevel.FirstLevel,
            ["second"] = AppealLevel.SecondLevel,
            ["external"] = AppealLevel.ExternalReview,
            ["ire"] = AppealLevel.ExternalReview,
            ["independentreview"] = AppealLevel.ExternalReview,
            ["statefairhearing"] = AppealLevel.ExternalReview,
            ["fairhearing"] = AppealLevel.ExternalReview
        };

    private static readonly IReadOnlyDictionary<string, LineOfBusiness> LineOfBusinessAliases =
        new Dictionary<string, LineOfBusiness>(StringComparer.OrdinalIgnoreCase)
        {
            ["medicareadvantage"] = LineOfBusiness.Medicare,
            ["ma"] = LineOfBusiness.Medicare,
            ["partc"] = LineOfBusiness.Medicare,
            ["medicarepartc"] = LineOfBusiness.Medicare,
            ["partd"] = LineOfBusiness.MedicarePartD,
            ["pdp"] = LineOfBusiness.MedicarePartD,
            ["exchange"] = LineOfBusiness.Marketplace,
            ["qhp"] = LineOfBusiness.Marketplace
        };

    /// <summary>
    /// Map Task.code → <see cref="AppealType"/> and the appealLevel
    /// extension → <see cref="AppealLevel"/>, then reconcile: either one
    /// marking external review makes the other external review too when
    /// it was absent, so the submission lands on the external-review clock
    /// regardless of which of the two the client populated. A grievance is
    /// never promoted to external review.
    /// </summary>
    internal static (AppealType Type, AppealLevel Level) ResolveTypeAndLevel(Hl7.Fhir.Model.Task task)
    {
        var typeCode = task.Code?.Coding.FirstOrDefault()?.Code;
        var appealType = ParseCode(typeCode, AppealType.Reconsideration, AppealTypeAliases, "Task.code");

        var levelExt = task.Extension.FirstOrDefault(e => e.Url == FhirAppealMapper.AppealLevelExtensionUrl);
        var levelCode = (levelExt?.Value as Code)?.Value;
        var appealLevel = ParseCode(levelCode, AppealLevel.FirstLevel, AppealLevelAliases,
            $"Task.extension('{FhirAppealMapper.AppealLevelExtensionUrl}')");

        if (appealType == AppealType.ExternalReview && string.IsNullOrEmpty(levelCode))
            appealLevel = AppealLevel.ExternalReview;
        else if (appealLevel == AppealLevel.ExternalReview && string.IsNullOrEmpty(typeCode))
            appealType = AppealType.ExternalReview;

        return (appealType, appealLevel);
    }

    private static TEnum ParseCodedExtension<TEnum>(
        Hl7.Fhir.Model.Task task, string extensionUrl, TEnum fallback,
        IReadOnlyDictionary<string, TEnum> aliases) where TEnum : struct, Enum
    {
        var ext = task.Extension.FirstOrDefault(e => e.Url == extensionUrl);
        return ParseCode((ext?.Value as Code)?.Value, fallback, aliases, $"Task.extension('{extensionUrl}')");
    }

    internal static TEnum ParseCode<TEnum>(
        string? value, TEnum fallback, IReadOnlyDictionary<string, TEnum> aliases, string location)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var normalized = new string(value.Where(c => c is not ('-' or '_' or ' ')).ToArray());
        if (!int.TryParse(normalized, out _)
            && Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }
        if (aliases.TryGetValue(normalized, out var aliased)) return aliased;

        throw new AppealSubmitValueException(
            $"Unrecognized {typeof(TEnum).Name} code '{value}'. Allowed: " +
            string.Join(", ", Enum.GetNames<TEnum>()) + ".",
            location);
    }

    internal static AppealNoteDto CommunicationToNoteDto(Communication communication)
    {
        var payload = communication.Payload.FirstOrDefault()?.Content as FhirString;
        return new AppealNoteDto
        {
            NoteId = communication.Id ?? Guid.NewGuid().ToString(),
            CreatedBy = StripPrefix("Practitioner/", communication.Sender?.Reference) ?? "unknown",
            NoteText = payload?.Value ?? string.Empty,
            IsInternal = communication.Category
                .SelectMany(c => c.Coding)
                .Any(c => c.Code == "internal"),
            CreatedAt = communication.Sent is { } sent
                ? (DateTimeOffset.TryParse(sent, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed.UtcDateTime
                    : DateTime.UtcNow)
                : DateTime.UtcNow
        };
    }

    internal static AppealAttachmentDto DocumentReferenceToAttachmentDto(DocumentReference doc)
    {
        var content = doc.Content.FirstOrDefault()?.Attachment;
        var transmission = doc.Extension
            .FirstOrDefault(e => e.Url == FhirAppealMapper.AppealX12TransmissionCodeExtensionUrl);
        var controlNumber = doc.Extension
            .FirstOrDefault(e => e.Url == FhirAppealMapper.AppealX12ControlNumberExtensionUrl);

        return new AppealAttachmentDto
        {
            AttachmentId = doc.Id ?? Guid.NewGuid().ToString(),
            AttachmentTypeCode = doc.Type?.Coding.FirstOrDefault()?.Code ?? "OZ",
            AttachmentTypeDescription = doc.Type?.Coding.FirstOrDefault()?.Display,
            TransmissionCode = (transmission?.Value as Code)?.Value ?? "EL",
            ControlNumber = (controlNumber?.Value as FhirString)?.Value,
            FileName = content?.Title,
            BlobUrl = content?.Url,
            ContentType = content?.ContentType,
            Description = doc.Description,
            UploadedAt = doc.Date.GetValueOrDefault(DateTimeOffset.UtcNow).UtcDateTime
        };
    }

    // ── OperationOutcome assembly ───────────────────────────────────────

    internal static OperationOutcome BuildOperationOutcome(
        IReadOnlyList<AppealSubmitChildOutcome> outcomes, string correlationId)
    {
        var outcome = new OperationOutcome
        {
            Id = Guid.NewGuid().ToString("N"),
            Meta = new Meta { LastUpdated = DateTimeOffset.UtcNow }
        };

        outcome.Extension =
        [
            new Extension
            {
                Url = "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-correlation-id",
                Value = new FhirString(correlationId)
            }
        ];

        foreach (var child in outcomes)
        {
            var issue = new OperationOutcome.IssueComponent
            {
                Severity = child.Success
                    ? OperationOutcome.IssueSeverity.Information
                    : OperationOutcome.IssueSeverity.Error,
                Code = child.Success
                    ? OperationOutcome.IssueType.Informational
                    : MapFailureToIssueType(child.FailureKind),
                Diagnostics = BuildIssueDiagnostics(child),
                Location = new[] { $"Bundle.entry[{child.EntryIndex}].resource" }
            };

            // Add cho-appeal-child-ref extension for correlation
            issue.Extension ??= new List<Extension>();
            issue.Extension.Add(new Extension
            {
                Url = "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-child-ref",
                Value = new FhirString(child.ChildRef)
            });

            if (!child.Success && !string.IsNullOrEmpty(child.RetryUrl))
            {
                issue.Extension =
                [
                    new Extension
                    {
                        Url = "http://fhir.cloudhealthoffice.com/StructureDefinition/cho-appeal-retry-url",
                        Value = new FhirUri(child.RetryUrl)
                    }
                ];
            }

            outcome.Issue.Add(issue);
        }

        return outcome;
    }

    private static OperationOutcome.IssueType MapFailureToIssueType(AppealSubmitFailureKind kind) =>
        kind switch
        {
            AppealSubmitFailureKind.Processing => OperationOutcome.IssueType.Processing,
            AppealSubmitFailureKind.Transient => OperationOutcome.IssueType.Transient,
            _ => OperationOutcome.IssueType.Exception
        };

    private static string BuildIssueDiagnostics(AppealSubmitChildOutcome child)
    {
        if (child.Success)
        {
            var status = child.HttpStatus?.ToString() ?? "200";
            return $"{child.Kind} {child.ChildRef} accepted (HTTP {status}, assignedId={child.AssignedId}).";
        }

        return $"{child.Kind} {child.ChildRef} failed: {child.Diagnostics}. " +
               $"Retry: {child.RetryUrl ?? "(none)"}.";
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string? StripPrefix(string prefix, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
    }

    private static bool HasExtensionTrue(Hl7.Fhir.Model.Task task, string extensionUrl)
    {
        var ext = task.Extension.FirstOrDefault(e => e.Url == extensionUrl);
        return (ext?.Value as FhirBoolean)?.Value == true;
    }

    private static DateTime? ReadDateTimeExtension(Hl7.Fhir.Model.Task task, string extensionUrl)
    {
        var ext = task.Extension.FirstOrDefault(e => e.Url == extensionUrl);
        var dt = ext?.Value as FhirDateTime;
        if (dt?.Value is null) return null;
        return DateTimeOffset.TryParse(dt.Value, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static OperationOutcome BuildOutcome(
        OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code,
        string diagnostics,
        string? location = null)
    {
        var issue = new OperationOutcome.IssueComponent
        {
            Severity = severity,
            Code = code,
            Diagnostics = diagnostics
        };
        if (!string.IsNullOrEmpty(location)) issue.Expression = [location];
        return new OperationOutcome { Issue = [issue] };
    }
}

/// <summary>
/// A coded value in the submitted Task (Task.code, appealLevel or
/// lineOfBusiness extension) that does not map to an appeals-service
/// enum. Surfaces as a 422 OperationOutcome with
/// <c>issue.code = code-invalid</c> and the FHIRPath of the offending
/// element in <c>issue.expression</c>.
/// </summary>
internal sealed class AppealSubmitValueException : InvalidOperationException
{
    public string Location { get; }

    public AppealSubmitValueException(string message, string location) : base(message)
    {
        Location = location;
    }
}
