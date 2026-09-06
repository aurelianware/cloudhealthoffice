namespace FhirService.Services.Cdex;

/// <summary>
/// Canonical URLs and code systems from the Da Vinci Clinical Data Exchange
/// (CDex) implementation guide, plus the two CHO-owned identifier systems the
/// additional-information exchange needs.
///
/// These are HL7's URLs, quoted — not CHO's. They are declared here so a
/// resource can claim conformance to the CDex profile it is actually shaped for,
/// and so the CapabilityStatement advertises the same canonicals the resources
/// carry.
///
/// WHICH CDex INTERACTION. Cloud Health Office is the payer, and the exchange it
/// needs is the SOLICITED one: a payer asks a provider for documentation on a
/// prior authorization it has pended, and the provider sends it back. In CDex
/// that is the Task Attachment Request profile for the request half and the
/// <c>$submit-attachment</c> operation for the response half. The CDex "Task
/// Data Request" profile — the payer querying a provider's clinical record — is
/// a different transaction and is NOT what a pended prior authorization needs.
/// </summary>
public static class CdexCanonicalUrls
{
    public const string ImplementationGuide = "http://hl7.org/fhir/us/davinci-cdex";

    /// <summary>
    /// Task profile for a payer's request for attachments on a claim or prior
    /// authorization. The request half of the round trip.
    /// </summary>
    public const string TaskAttachmentRequestProfile =
        "http://hl7.org/fhir/us/davinci-cdex/StructureDefinition/cdex-task-attachment-request";

    /// <summary>The operation a provider invokes to send the requested documentation.</summary>
    public const string SubmitAttachmentOperation =
        "http://hl7.org/fhir/us/davinci-cdex/OperationDefinition/submit-attachment";

    /// <summary>The operation's name as it appears on the wire and in the CapabilityStatement.</summary>
    public const string SubmitAttachmentOperationName = "submit-attachment";

    /// <summary>The route the operation is served at, relative to the FHIR base.</summary>
    public const string SubmitAttachmentRoute = "$submit-attachment";

    // ── CDex temporary code system ───────────────────────────────────────────
    // CDex carries workflow codes that have no permanent home yet in a temporary
    // code system. The codes below are the ones this exchange uses.

    public const string TempCodeSystem = "http://hl7.org/fhir/us/davinci-cdex/CodeSystem/cdex-temp";

    /// <summary><c>Task.code</c> — this Task is a request for attachments.</summary>
    public const string AttachmentRequestCode = "attachment-request-code";

    /// <summary><c>Task.input.type</c> — the document type being asked for.</summary>
    public const string AttachmentCode = "attachment-code";

    /// <summary><c>Task.input.type</c> — the claim/PA line the request is about.</summary>
    public const string LineItem = "line-item";

    /// <summary><c>Task.input.type</c> — why the payer needs the data.</summary>
    public const string PurposeOfUse = "purpose-of-use";

    /// <summary><c>Task.input.type</c> — whether a signature is required on the response.</summary>
    public const string SignatureFlag = "signature-flag";

    // ── External code systems ────────────────────────────────────────────────

    /// <summary>LOINC — the document-type vocabulary CDex and the Attachments IG use.</summary>
    public const string Loinc = "http://loinc.org";

    /// <summary>X12 PWK attachment-type codes, carried alongside LOINC so one request serves both wires.</summary>
    public const string X12AttachmentReportType = "https://codesystem.x12.org/005010/755";

    /// <summary>X12 306 review-decision codes. A4 is the pended-for-information decision.</summary>
    public const string X12ReviewDecision = "https://codesystem.x12.org/005010/306";

    /// <summary>HL7 v3 ActReason — <c>COVAUTH</c> is coverage authorization.</summary>
    public const string ActReason = "http://terminology.hl7.org/CodeSystem/v3-ActReason";

    public const string CoverageAuthPurposeOfUse = "COVAUTH";

    /// <summary>US NPI, for identifying the provider expected to answer.</summary>
    public const string UsNpi = "http://hl7.org/fhir/sid/us-npi";

    /// <summary>ICD-10-CM, for the diagnosis context on a requested item.</summary>
    public const string Icd10Cm = "http://hl7.org/fhir/sid/icd-10-cm";

    /// <summary>HCPCS/CPT, for the service line a requested item is about.</summary>
    public const string Hcpcs = "http://terminology.hl7.org/CodeSystem/HCPCS-all-x-codes";

    // ── CHO identifier systems ───────────────────────────────────────────────
    // Permanent: once a Task claims one of these on Task.identifier, the value
    // cannot move without breaking every correlation already in flight.

    /// <summary>The tracking id / attachment control number a submission quotes.</summary>
    public const string TrackingIdSystem = "http://cloudhealthoffice.com/rfai-tracking-id";

    /// <summary>The prior-authorization number the request belongs to.</summary>
    public const string AuthorizationNumberSystem = "http://cloudhealthoffice.com/prior-authorization";

    /// <summary>Identity of one submitted artifact, for the response linkage on <c>Task.output</c>.</summary>
    public const string SubmissionIdSystem = "http://cloudhealthoffice.com/rfai-submission-id";

    /// <summary>
    /// CHO's own RFAI state, carried on <c>Task.businessStatus</c> so the
    /// internal state name survives the translation into FHIR's narrower
    /// <c>Task.status</c> vocabulary.
    /// </summary>
    public const string RfaiStatusCodeSystem = ChoFhirCanonicalUrls.CodeSystemBase + "cho-rfai-status";

    /// <summary>
    /// CHO-owned <c>Task.input.type</c> codes, for request context CDex has no
    /// code of its own for.
    ///
    /// Named in CHO's system rather than by overloading a CDex code: typing a
    /// diagnosis as <c>attachment-code</c> would make a consumer reading
    /// <c>Task.input</c> by type take the diagnosis for a document being asked
    /// for. A code CHO owns is honest about being CHO's.
    /// </summary>
    public const string ChoTaskInputCodeSystem =
        ChoFhirCanonicalUrls.CodeSystemBase + "cho-rfai-task-input";

    /// <summary>
    /// <c>Task.input.type</c> — the diagnosis a requested item is about. Its
    /// value is an ICD-10-CM CodeableConcept.
    /// </summary>
    public const string DiagnosisContext = "diagnosis-context";
}
