using System.Text.Json.Serialization;
using FhirService.Services.Cdex;
using Hl7.Fhir.Model;
using FhirTask = Hl7.Fhir.Model.Task;

namespace FhirService.Services.Pas;

/// <summary>
/// Which documentation the plan needs before it can decide a service (PAS-07),
/// from <c>Cms0057:PasAdditionalInformation</c>. A pended PAS request for a
/// service listed here asks the provider for what it lacks, through a CDex
/// request in rfai-service, and the PAS response carries that ask as a Task
/// and CommunicationRequests. A service listed nowhere asks for nothing: CHO
/// never invents a documentation request no rule named.
/// </summary>
public sealed class PasAdditionalInformationOptions
{
    public const string SectionName = "Cms0057:PasAdditionalInformation";

    public List<PasDocumentationRequirement> Requirements { get; set; } = new();

    /// <summary>Calendar days the provider has to send what is asked for.</summary>
    public int ResponseDays { get; set; } = 14;
}

/// <summary>What one set of services needs.</summary>
public sealed class PasDocumentationRequirement
{
    /// <summary>
    /// <c>Claim.item.productOrService</c> codes (CPT, HCPCS or an X12 1365
    /// service type) this requirement covers.
    /// </summary>
    public List<string> ServiceCodes { get; set; } = new();

    /// <summary>When set, a code matches only in this system.</summary>
    public string? ServiceCodeSystem { get; set; }

    public List<PasRequestedAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// Canonicals of DTR forms required for these services, in addition to the
    /// forms DTR serves for the line's codes. A form the request already
    /// answered is not asked for again.
    /// </summary>
    public List<string> Questionnaires { get; set; } = new();
}

/// <summary>One document asked for.</summary>
public sealed class PasRequestedAttachment
{
    /// <summary>LOINC attachment code, from <c>valid-hl7-attachment-requests</c> (e.g. 28570-0 Procedure note).</summary>
    public string LoincCode { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>X12 PWK01 report type (code set 755), for the 278 and the CommunicationRequest category.</summary>
    public string? PwkCode { get; set; }

    /// <summary>LOINC attachment modifier (class LP7763-8), e.g. 18804-5 (last 3 months).</summary>
    public string? ContentModifier { get; set; }
}

/// <summary>One requested item, as authorization-service and rfai-service take it.</summary>
public sealed class PasRequestedInformationItem
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("loincCode")]
    public string? LoincCode { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("serviceLineProcedureCode")]
    public string? ServiceLineProcedureCode { get; set; }

    [JsonPropertyName("diagnosisCode")]
    public string? DiagnosisCode { get; set; }

    [JsonPropertyName("serviceLineNumber")]
    public int? ServiceLineNumber { get; set; }

    [JsonPropertyName("contentModifier")]
    public string? ContentModifier { get; set; }

    [JsonPropertyName("questionnaire")]
    public string? Questionnaire { get; set; }
}

/// <summary>
/// Works out what a pended request lacks: for each line a requirement covers,
/// the documents it names and the forms it (or DTR) needs that the request did
/// not answer. Pure; the forms DTR serves are looked up by the caller.
/// </summary>
public static class PasAdditionalInformationPlanner
{
    /// <summary>rfai-service's limit on items in one request.</summary>
    public const int MaxItems = 25;

    public const string PendReason =
        "Pended: additional documentation is required before the request can be reviewed.";

    /// <summary>
    /// The items to ask for, or none. <paramref name="dtrFormsByLine"/> holds,
    /// per line sequence, the canonicals of the forms DTR serves for that line's
    /// codes; <paramref name="answered"/> the forms the request answered.
    /// </summary>
    public static List<PasRequestedInformationItem> Plan(
        Claim claim,
        PasAdditionalInformationOptions options,
        IReadOnlyCollection<string> answered,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? dtrFormsByLine = null)
    {
        var items = new List<PasRequestedInformationItem>();
        if (options.Requirements.Count == 0)
            return items;

        var answeredUrls = new HashSet<string>(answered.Select(CanonicalUrl), StringComparer.Ordinal);

        foreach (var line in claim.Item)
        {
            var requirements = options.Requirements.Where(r => Covers(r, line)).ToList();
            if (requirements.Count == 0) continue;

            var sequence = line.Sequence;
            var procedure = line.ProductOrService?.Coding.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Code))?.Code;
            var diagnosis = DiagnosisFor(claim, line);

            foreach (var attachment in requirements.SelectMany(r => r.Attachments)
                         .Where(a => !string.IsNullOrWhiteSpace(a.LoincCode) && !string.IsNullOrWhiteSpace(a.Description))
                         .DistinctBy(a => (a.LoincCode, a.ContentModifier)))
            {
                items.Add(new PasRequestedInformationItem
                {
                    Code = string.IsNullOrWhiteSpace(attachment.PwkCode) ? null : attachment.PwkCode.Trim(),
                    LoincCode = attachment.LoincCode.Trim(),
                    Description = attachment.Description.Trim(),
                    ContentModifier = string.IsNullOrWhiteSpace(attachment.ContentModifier) ? null : attachment.ContentModifier.Trim(),
                    ServiceLineNumber = sequence,
                    ServiceLineProcedureCode = procedure,
                    DiagnosisCode = diagnosis,
                });
            }

            var forms = requirements.SelectMany(r => r.Questionnaires)
                .Concat(dtrFormsByLine is not null && sequence is { } s && dtrFormsByLine.TryGetValue(s, out var dtr) ? dtr : [])
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(q => q.Trim())
                .DistinctBy(CanonicalUrl, StringComparer.Ordinal)
                .Where(q => !answeredUrls.Contains(CanonicalUrl(q)));
            foreach (var form in forms)
            {
                items.Add(new PasRequestedInformationItem
                {
                    Description = "Complete the documentation questionnaire for this service.",
                    Questionnaire = form,
                    ServiceLineNumber = sequence,
                    ServiceLineProcedureCode = procedure,
                    DiagnosisCode = diagnosis,
                });
            }
        }

        return items.Take(MaxItems).ToList();
    }

    /// <summary>When the documentation is due: <see cref="PasAdditionalInformationOptions.ResponseDays"/> after the decision.</summary>
    public static DateTime DueDate(DateTime decidedAt, PasAdditionalInformationOptions options)
        => decidedAt.AddDays(Math.Clamp(options.ResponseDays, 1, 90));

    private static bool Covers(PasDocumentationRequirement requirement, Claim.ItemComponent line)
        => line.ProductOrService?.Coding.Any(c =>
               !string.IsNullOrWhiteSpace(c.Code)
               && requirement.ServiceCodes.Contains(c.Code, StringComparer.OrdinalIgnoreCase)
               && (requirement.ServiceCodeSystem is null
                   || string.Equals(requirement.ServiceCodeSystem, c.System, StringComparison.Ordinal))) == true;

    /// <summary>The line's first diagnosis (by <c>diagnosisSequence</c>), as its code.</summary>
    private static string? DiagnosisFor(Claim claim, Claim.ItemComponent line)
    {
        foreach (var sequence in line.DiagnosisSequence.Where(d => d.HasValue).Select(d => d!.Value))
        {
            var diagnosis = claim.Diagnosis.FirstOrDefault(d => d.Sequence == sequence);
            if ((diagnosis?.Diagnosis as CodeableConcept)?.Coding.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Code))?.Code is { } code)
                return code;
        }

        return null;
    }

    /// <summary>A canonical without its <c>|version</c>.</summary>
    public static string CanonicalUrl(string canonical)
    {
        var bar = canonical.IndexOf('|');
        return (bar < 0 ? canonical : canonical[..bar]).Trim();
    }
}

/// <summary>
/// The PAS form of a CDex additional-information request: one PAS Task per kind
/// of ask (documents, forms), and, on <c>$submit</c>, one PAS CommunicationRequest
/// per document asked for (the 278 HCR/PWK request), referenced from the
/// ClaimResponse. Built from the request as rfai-service holds it, so the
/// response states what the payer is actually waiting for, with the tracking
/// id the provider quotes on <c>$submit-attachment</c>.
/// </summary>
public static class PasAdditionalInformationResources
{
    public const string TaskProfile = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-task";
    public const string CommunicationRequestProfile = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/profile-communicationrequest";
    public const string PasTempCodes = "http://hl7.org/fhir/us/davinci-pas/CodeSystem/PASTempCodes";
    public const string ServiceLineNumberUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-serviceLineNumber";
    public const string ContentModifierUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-contentModifier";
    public const string CommunicatedDiagnosisUrl = "http://hl7.org/fhir/us/davinci-pas/StructureDefinition/extension-communicatedDiagnosis";
    public const string X12ReportTypeSystem = "https://codesystem.x12.org/005010/755";
    public const string Loinc = "http://loinc.org";
    public const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";
    public const string UsNpi = "http://hl7.org/fhir/sid/us-npi";

    /// <summary>The payer's identifier when the request's insurer carries none.</summary>
    public const string ChoPayerSystem = "http://cloudhealthoffice.com/fhir/payer";

    /// <summary>
    /// Adds the request's Tasks (and, when <paramref name="withCommunicationRequests"/>,
    /// its CommunicationRequests) to a PAS response bundle whose first entry is
    /// the ClaimResponse. <paramref name="payerUrl"/> is where the provider
    /// sends what is asked for (<c>$submit-attachment</c>).
    /// </summary>
    public static void Attach(
        Bundle bundle, CdexAdditionalInformationRequest request, string payerUrl, bool withCommunicationRequests)
    {
        if (bundle.Entry.FirstOrDefault()?.Resource is not ClaimResponse claimResponse
            || request.RequestedItems.Count == 0)
            return;

        var insurerIdentifier = IdentifierOf(bundle, claimResponse.Insurer)
                                ?? new Identifier(ChoPayerSystem, request.TenantId);
        var providerIdentifier = IdentifierOf(bundle, claimResponse.Requestor, UsNpi)
                                 ?? (string.IsNullOrWhiteSpace(request.RequestingProviderNpi)
                                     ? null
                                     : new Identifier(UsNpi, request.RequestingProviderNpi));

        var documents = request.RequestedItems.Where(i => string.IsNullOrWhiteSpace(i.Questionnaire)
                                                          && !string.IsNullOrWhiteSpace(i.LoincCode)).ToList();
        var forms = request.RequestedItems.Where(i => !string.IsNullOrWhiteSpace(i.Questionnaire)).ToList();

        if (documents.Count > 0)
            Add(bundle, BuildTask(request, claimResponse, insurerIdentifier, providerIdentifier, payerUrl,
                "attachment-request-code", "Attachment Request Code", documents));
        if (forms.Count > 0)
            Add(bundle, BuildTask(request, claimResponse, insurerIdentifier, providerIdentifier, payerUrl,
                "attachment-request-questionnaire", "Attachment Request Questionnaire", forms));

        if (!withCommunicationRequests) return;

        for (var i = 0; i < documents.Count; i++)
        {
            var communicationRequest = BuildCommunicationRequest(request, claimResponse, documents[i], i + 1);
            var fullUrl = Add(bundle, communicationRequest);
            claimResponse.CommunicationRequest.Add(new ResourceReference(fullUrl));
        }
    }

    /// <summary>
    /// Adds the resource under the ClaimResponse's base (a <c>urn:uuid</c>
    /// only when the ClaimResponse has one), with every reference it makes to
    /// a resource in the bundle written as that entry's fullUrl: a relative
    /// reference in an entry resolves against that entry's own base, and the
    /// bundle echoes the request's resources under the request's base.
    /// </summary>
    private static string Add(Bundle bundle, Resource resource)
    {
        var anchor = bundle.Entry.FirstOrDefault()?.FullUrl;
        var at = anchor?.LastIndexOf("/ClaimResponse/", StringComparison.Ordinal) ?? -1;
        var fullUrl = at > 0 && anchor!.StartsWith("http", StringComparison.Ordinal)
            ? $"{anchor[..at]}/{resource.TypeName}/{resource.Id}"
            : $"urn:uuid:{resource.Id}";

        foreach (var reference in References(resource))
        {
            if (string.IsNullOrWhiteSpace(reference.Reference)) continue;
            var target = bundle.Entry.FirstOrDefault(e => e.Resource is not null && !string.IsNullOrWhiteSpace(e.FullUrl)
                && (string.Equals(e.FullUrl, reference.Reference, StringComparison.Ordinal)
                    || string.Equals($"{e.Resource.TypeName}/{e.Resource.Id}", reference.Reference, StringComparison.Ordinal)));
            if (target is not null)
                reference.Reference = target.FullUrl;
        }

        bundle.Entry.Add(new Bundle.EntryComponent { FullUrl = fullUrl, Resource = resource });
        return fullUrl;
    }

    private static IEnumerable<ResourceReference> References(Base element)
    {
        if (element is ResourceReference reference) yield return reference;
        foreach (var child in element.Children)
            foreach (var nested in References(child))
                yield return nested;
    }

    private static FhirTask BuildTask(
        CdexAdditionalInformationRequest request, ClaimResponse claimResponse, Identifier insurer,
        Identifier? provider, string payerUrl, string code, string display, List<CdexRequestedItem> items)
    {
        var (status, reason) = StatusOf(request);
        var task = new FhirTask
        {
            Id = Guid.NewGuid().ToString(),
            Meta = new Meta { Profile = [TaskProfile] },
            // The handle the provider quotes when it answers.
            Identifier = [new Identifier(CdexCanonicalUrls.TrackingIdSystem, request.TrackingId)],
            Status = status,
            StatusReason = new CodeableConcept { Text = reason },
            Intent = FhirTask.TaskIntent.Order,
            Code = new CodeableConcept(PasTempCodes, code, display),
            For = claimResponse.Patient is { } patient ? new ResourceReference(patient.Reference) : null,
            AuthoredOn = request.CreatedAt == default ? null : new FhirDateTime(new DateTimeOffset(DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc))).Value,
            // The payer asks; the requesting provider is to answer.
            Requester = new ResourceReference { Reference = claimResponse.Insurer?.Reference, Identifier = insurer },
            Owner = provider is null ? null : new ResourceReference { Reference = claimResponse.Requestor?.Reference, Identifier = provider },
            ReasonCode = new CodeableConcept(PasTempCodes, "priorAuthorization", "Prior Authorization Information Request"),
            ReasonReference = claimResponse.Request is { } claim ? (ResourceReference)claim.DeepCopy() : null,
        };

        if (request.DueDate is { } due)
        {
            task.Restriction = new FhirTask.RestrictionComponent
            {
                Period = new Period
                {
                    Start = request.CreatedAt == default ? null : Instant(request.CreatedAt),
                    End = Instant(due),
                },
            };
        }

        task.Input.Add(new FhirTask.ParameterComponent
        {
            Type = new CodeableConcept(PasTempCodes, "payer-url", "Payer URL"),
            Value = new FhirUrl(payerUrl),
        });

        foreach (var item in items)
        {
            var input = string.IsNullOrWhiteSpace(item.Questionnaire)
                ? new FhirTask.ParameterComponent
                {
                    Type = new CodeableConcept(PasTempCodes, "attachments-needed", "Attachments Needed"),
                    Value = new CodeableConcept(Loinc, item.LoincCode!, item.Description),
                }
                : new FhirTask.ParameterComponent
                {
                    Type = new CodeableConcept(PasTempCodes, "questionnaire-context", "Questionnaire Context"),
                    Value = new FhirString(item.Questionnaire),
                };

            if (item.ServiceLineNumber is { } line)
                input.Extension.Add(new Extension(ServiceLineNumberUrl, new PositiveInt(line)));
            if (string.IsNullOrWhiteSpace(item.Questionnaire) && !string.IsNullOrWhiteSpace(item.ContentModifier))
                input.Extension.Add(new Extension(ContentModifierUrl, new CodeableConcept(Loinc, item.ContentModifier)));

            task.Input.Add(input);
        }

        return task;
    }

    private static CommunicationRequest BuildCommunicationRequest(
        CdexAdditionalInformationRequest request, ClaimResponse claimResponse, CdexRequestedItem item, int index)
    {
        var communicationRequest = new CommunicationRequest
        {
            Id = Guid.NewGuid().ToString(),
            Meta = new Meta { Profile = [CommunicationRequestProfile] },
            Identifier = [new Identifier(CdexCanonicalUrls.TrackingIdSystem, $"{request.TrackingId}-{index}")],
            Status = request.Status switch
            {
                CdexAdditionalInformationStatus.Open => RequestStatus.Active,
                CdexAdditionalInformationStatus.Cancelled => RequestStatus.Revoked,
                _ => RequestStatus.Completed,
            },
            // Sent electronically, through CDex.
            Medium = [new CodeableConcept(PasTempCodes, "CDEX", "CDex")],
            Requester = claimResponse.Insurer?.Reference is { } insurer ? new ResourceReference(insurer) : null,
            Sender = claimResponse.Requestor?.Reference is { } requestor ? new ResourceReference(requestor) : null,
            AuthoredOn = request.CreatedAt == default ? null : new FhirDateTime(new DateTimeOffset(DateTime.SpecifyKind(request.CreatedAt, DateTimeKind.Utc))).Value,
        };

        if (claimResponse.Requestor?.Reference is { } recipient)
            communicationRequest.Recipient.Add(new ResourceReference(recipient));
        if (item.ServiceLineNumber is { } line)
            communicationRequest.Extension.Add(new Extension(ServiceLineNumberUrl, new PositiveInt(line)));
        if (!string.IsNullOrWhiteSpace(item.Code))
            communicationRequest.Category.Add(new CodeableConcept(X12ReportTypeSystem, item.Code));
        if (request.DueDate is { } due)
            communicationRequest.Occurrence = new Period { End = Instant(due) };

        var payload = new CommunicationRequest.PayloadComponent { Content = new FhirString(item.LoincCode) };
        if (!string.IsNullOrWhiteSpace(item.DiagnosisCode))
            payload.Extension.Add(new Extension(CommunicatedDiagnosisUrl, new CodeableConcept(Icd10Cm, item.DiagnosisCode)));
        if (!string.IsNullOrWhiteSpace(item.ContentModifier))
            payload.Extension.Add(new Extension(ContentModifierUrl, new CodeableConcept(Loinc, item.ContentModifier)));
        communicationRequest.Payload.Add(payload);

        return communicationRequest;
    }

    /// <summary>The case's lifecycle as an HRex task status, with the reason in words.</summary>
    private static (FhirTask.TaskStatus Status, string Reason) StatusOf(CdexAdditionalInformationRequest request)
        => request.Status switch
        {
            CdexAdditionalInformationStatus.Open =>
                (FhirTask.TaskStatus.Requested, "Awaiting the requested documentation from the provider."),
            CdexAdditionalInformationStatus.DocsReceived =>
                (FhirTask.TaskStatus.Received, "Documentation received; the request is back in review."),
            CdexAdditionalInformationStatus.Cancelled =>
                (FhirTask.TaskStatus.Cancelled, "The payer withdrew the request for documentation."),
            _ => (FhirTask.TaskStatus.Completed, "The request for documentation is closed."),
        };

    private static string Instant(DateTime value)
        => new FhirDateTime(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))).Value;

    /// <summary>The first identifier (in <paramref name="system"/>, when given) of the bundle resource a reference names.</summary>
    private static Identifier? IdentifierOf(Bundle bundle, ResourceReference? reference, string? system = null)
    {
        if (reference is null) return null;
        if (reference.Identifier is { } own && (system is null || own.System == system))
            return (Identifier)own.DeepCopy();
        if (string.IsNullOrWhiteSpace(reference.Reference)) return null;

        var target = bundle.Entry.FirstOrDefault(e =>
            e.Resource is not null
            && (string.Equals(e.FullUrl, reference.Reference, StringComparison.Ordinal)
                || (e.FullUrl?.EndsWith("/" + reference.Reference, StringComparison.Ordinal) ?? false)
                || string.Equals($"{e.Resource.TypeName}/{e.Resource.Id}", reference.Reference, StringComparison.Ordinal)))?.Resource;

        var identifiers = target switch
        {
            Organization o => o.Identifier,
            Practitioner p => p.Identifier,
            PractitionerRole r => r.Identifier,
            _ => null,
        };

        var found = identifiers?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Value) && (system is null || i.System == system));
        return found is null ? null : (Identifier)found.DeepCopy();
    }
}
