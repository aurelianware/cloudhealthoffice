using System.ComponentModel.DataAnnotations;
using CloudHealthOffice.Infrastructure.Contacts;
using System.Text.Json.Serialization;
using MongoDB.Bson.Serialization.Attributes;

namespace ContactService.Models;

/// <summary>
/// One contact with a member, a provider or someone acting for them: a call,
/// a chat, an email, a walk-in. The record a service representative works in.
///
/// A contact is not a case. A grievance, a coverage request and an appeal are
/// cases, with their own clocks, and they live in appeals-service and
/// authorization-service. The contact records that the conversation happened,
/// what it was about, and what it touched; when it opens a case, the case
/// carries the contact's start time as its received date.
///
/// See docs/architecture/cho-service.md §3.
/// </summary>
[BsonIgnoreExtraElements]
public class Contact
{
    /// <summary>Owning tenant. Always set server-side from the caller's token.</summary>
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Optimistic-concurrency revision, bumped by every write. A rep and a
    /// supervisor working the same contact cannot silently overwrite each
    /// other.
    /// </summary>
    public long Revision { get; set; }

    public ContactCaseIntent? CaseIntent { get; set; }
    public string CaseDeliveryStatus { get; set; } = "Pending";
    public List<ContactCaseAmendment> CaseAmendments { get; set; } = new();
    public ContactCaseResult? OpenedCase { get; set; }
    public List<ContactFollowUpIntent> FollowUps { get; set; } = new();

    /// <summary>Human-facing contact number, unique within the tenant.</summary>
    [Required]
    [StringLength(50)]
    public string ContactNumber { get; set; } = string.Empty;

    // ── How it arrived ────────────────────────────────────────────

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactChannel Channel { get; set; } = ContactChannel.Phone;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactDirection Direction { get; set; } = ContactDirection.Inbound;

    /// <summary>
    /// The phone platform's own call identifier, kept so a contact can be
    /// correlated with the platform's recording and its reporting. CHO does
    /// not route calls (cho-service.md §6.2); this is the only handle it
    /// keeps on the call itself.
    /// </summary>
    [StringLength(200)]
    public string? PhoneCallId { get; set; }

    // ── Who called ────────────────────────────────────────────────

    public ContactCaller Caller { get; set; } = new();

    /// <summary>
    /// The member the contact is about, once the rep has verified who they
    /// are speaking to. Null while the caller is unidentified, and on a
    /// provider contact about no one member.
    /// </summary>
    [StringLength(50)]
    public string? MemberId { get; set; }

    /// <summary>
    /// How the caller's identity was verified, in the rep's own words
    /// ("member ID and date of birth"). Recorded because the plan has to be
    /// able to show it, and because an IVR verifies the same way a rep does.
    /// Null means unverified, which blocks nothing in step 1 but is visible.
    /// </summary>
    [StringLength(200)]
    public string? MemberVerification { get; set; }

    /// <summary>The provider the contact is about, by NPI.</summary>
    [StringLength(10)]
    public string? ProviderNpi { get; set; }

    [StringLength(200)]
    public string? ProviderName { get; set; }

    // ── When, and who worked it ───────────────────────────────────

    public DateTime StartedAt { get; set; }

    /// <summary>Null while the contact is open.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Talk time: <see cref="StartedAt"/> to <see cref="EndedAt"/>, stored
    /// rather than derived so reporting does not recompute it per row and
    /// cannot disagree with what the screen showed. After-call work is the
    /// gap between <see cref="EndedAt"/> and <see cref="ClosedAt"/>.
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>The token subject of the rep who took it.</summary>
    [StringLength(200)]
    public string AgentId { get; set; } = string.Empty;

    [StringLength(200)]
    public string? AgentName { get; set; }

    /// <summary>
    /// The contact-centre queue the call came out of, as the phone platform
    /// names it. Free text: CHO does not own these queues.
    /// </summary>
    [StringLength(100)]
    public string? Queue { get; set; }

    // ── What it was about ─────────────────────────────────────────

    /// <summary>
    /// Reason codes from the tenant's own taxonomy
    /// (<see cref="ContactReason"/>). A contact can have more than one: one
    /// call is often a claim question and an ID card request.
    /// </summary>
    public List<string> ReasonCodes { get; set; } = new();

    /// <summary>
    /// What was discussed: the claims, authorizations, appeals and documents
    /// the rep pulled up. Linking a claim or an authorization also writes a
    /// contact note on it, so its own history shows the call.
    /// </summary>
    public List<ContactSubject> Subjects { get; set; } = new();

    /// <summary>
    /// What kind of contact this was, which decides what happens next and on
    /// what clock.
    ///
    /// Step 1 records it when the rep sets it and leaves it null otherwise.
    /// It is <b>not yet mandatory and opens no case</b>: making it mandatory,
    /// opening the grievance, coverage request or appeal dated from
    /// <see cref="StartedAt"/>, and running the rule-pack clocks is step 2 of
    /// cho-service.md §8. The field is here from the start so the step-2
    /// work is behaviour, not a migration.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactClassification? Classification { get; set; }

    [StringLength(200)]
    public string? ClassifiedBy { get; set; }

    public DateTime? ClassifiedAt { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactResolution? Resolution { get; set; }

    /// <summary>
    /// Work items opened to finish what the call could not. Step 3 of
    /// cho-service.md §8 fills this through work-management-service; step 1
    /// only carries it.
    /// </summary>
    public List<string> FollowUpWorkItemIds { get; set; } = new();

    /// <summary>
    /// The rep's wrap-up, in their own words. Step 5 drafts it with Claude
    /// and the rep edits and accepts; a person owns the text either way.
    /// </summary>
    [StringLength(MaxSummaryLength)]
    public string? Summary { get; set; }

    public const int MaxSummaryLength = 4000;

    // ── Recording and transcript ──────────────────────────────────

    /// <summary>
    /// Where the phone platform keeps the recording, and which platform that
    /// is. CHO stores the link, never the audio: recordings are large and
    /// sensitive, and the platform already holds them under the plan's BAA
    /// and its own retention rules (cho-service.md §9, question 2, decided
    /// 2026-10-22).
    /// </summary>
    [StringLength(1000)]
    public string? RecordingLink { get; set; }

    [StringLength(100)]
    public string? RecordingPlatform { get; set; }

    /// <summary>
    /// The transcript in the document store, which CHO does keep: it is text,
    /// it is what the AI wrap-up of step 5 reads, and it is what an auditor
    /// asks for.
    /// </summary>
    [StringLength(100)]
    public string? TranscriptDocumentId { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactStatus Status { get; set; } = ContactStatus.Open;

    /// <summary>
    /// When the rep finished their after-call work and closed the record.
    /// Distinct from <see cref="EndedAt"/>, which is when the caller hung up.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// Append-only: what changed on the contact, who changed it and when.
    /// A contact is a record of a conversation that a regulator may read, so
    /// corrections are added, never written over.
    /// </summary>
    public List<ContactRevisionEntry> History { get; set; } = new();

    public DateTime CreatedAt { get; set; }

    [StringLength(200)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }

    [StringLength(200)]
    public string? UpdatedBy { get; set; }
}

/// <summary>Who the plan was actually speaking to.</summary>
public class ContactCaller
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CallerType Type { get; set; } = CallerType.Member;

    [StringLength(200)]
    public string? Name { get; set; }

    [StringLength(50)]
    public string? Phone { get; set; }

    /// <summary>Set when the caller is a provider's office.</summary>
    [StringLength(10)]
    public string? Npi { get; set; }

    /// <summary>
    /// How a representative relates to the member ("spouse", "power of
    /// attorney", "the practice's billing service").
    /// </summary>
    [StringLength(100)]
    public string? Relationship { get; set; }

    /// <summary>
    /// Whether an appointment of representative is on file. A rep who
    /// discusses a case with someone other than the member needs one; the
    /// authority of record lives in personal-representative-service, and
    /// this is what the rep saw at the time.
    /// </summary>
    public bool? AorOnFile { get; set; }
}

/// <summary>
/// Something the contact was about: a claim, an authorization, an appeal, a
/// payment run, a document. Linking one is what makes the contact searchable
/// from the other side — "who called about this claim".
/// </summary>
public class ContactSubject
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactSubjectType Type { get; set; }

    /// <summary>The subject's own id in its service.</summary>
    [Required]
    [StringLength(100)]
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>
    /// The number a person would recognise: a claim number, an auth number.
    /// Display only; <see cref="SubjectId"/> is the handle.
    /// </summary>
    [StringLength(100)]
    public string? Reference { get; set; }

    /// <summary>What the caller asked about this one thing.</summary>
    [StringLength(1000)]
    public string? Note { get; set; }

    /// <summary>
    /// The note written on the subject itself (a claim note, for claims), so
    /// the claim's history shows the call. Null when the subject takes no
    /// notes, or when the note could not be written — the link still stands
    /// either way, because the contact is the record of the conversation.
    /// </summary>
    [StringLength(100)]
    public string? SubjectNoteId { get; set; }

    public DateTime LinkedAt { get; set; }

    [StringLength(200)]
    public string? LinkedBy { get; set; }
}

/// <summary>One entry in a contact's append-only history.</summary>
public class ContactRevisionEntry
{
    /// <summary>
    /// What happened: <c>Started</c>, <c>Updated</c>, <c>SubjectLinked</c>,
    /// <c>Classified</c>, <c>Closed</c>, <c>Reopened</c>.
    /// </summary>
    [StringLength(50)]
    public string Action { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Detail { get; set; }

    public DateTime At { get; set; }

    [StringLength(200)]
    public string By { get; set; } = string.Empty;
}

public enum ContactChannel
{
    Phone,
    Ivr,
    Chat,
    Email,
    Portal,
    WalkIn,
    Mail,
}

public enum ContactDirection
{
    Inbound,
    Outbound,
}

public enum CallerType
{
    Member,

    /// <summary>Someone acting for the member: family, an authorised representative.</summary>
    Representative,

    Provider,
    Other,
}

public enum ContactSubjectType
{
    Claim,
    Authorization,
    Appeal,
    Grievance,
    PaymentRun,
    Document,
}

/// <summary>
/// What kind of contact this was. The four that are not
/// <see cref="Inquiry"/> each open a case with a regulatory clock, dated from
/// the call — which step 2 of cho-service.md §8 implements.
/// </summary>
public enum ContactClassification
{
    /// <summary>Resolved on the call, or a follow-up on the plan's own service level.</summary>
    Inquiry,

    Grievance,
    ExpeditedGrievance,

    /// <summary>An organization determination: a request for the plan to cover something.</summary>
    CoverageRequest,

    Appeal,
    ExpeditedAppeal,
}

public enum ContactResolution
{
    ResolvedOnCall,
    FollowUpCreated,
    Transferred,
    CallbackScheduled,
}

public enum ContactStatus
{
    /// <summary>The conversation, or the rep's after-call work, is still going.</summary>
    Open,

    Closed,
}

public sealed class ContactCaseAmendment
{
    public ContactCaseIntent PreviousIntent { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
    public string By { get; set; } = string.Empty;
    public DateTime At { get; set; }
}
