using CloudHealthOffice.Infrastructure.Contacts;
using System.Text.Json.Serialization;

namespace CloudHealthOffice.Portal.Services;

/// <summary>
/// The contact wire shapes, mirroring contact-service's own
/// (<c>src/services/contact-service/Models</c>). The portal keeps its own
/// copies, as it does for claims and appeals, so a backend model change shows
/// up here as a compile error rather than a silently empty screen.
///
/// See docs/architecture/cho-service.md §3.
/// </summary>
public class ContactRecord
{
    public string Id { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public long Revision { get; set; }
    public ContactCaseIntent? CaseIntent { get; set; }
    public ContactCaseResult? OpenedCase { get; set; }
    public List<ContactFollowUpIntent> FollowUps { get; set; } = new();

    public ContactChannel Channel { get; set; }
    public ContactDirection Direction { get; set; }
    public ContactStatus Status { get; set; }

    public ContactCaller Caller { get; set; } = new();

    public string? MemberId { get; set; }
    public string? MemberVerification { get; set; }
    public string? ProviderNpi { get; set; }
    public string? ProviderName { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public DateTime? ClosedAt { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public string? Queue { get; set; }
    public string? PhoneCallId { get; set; }

    public List<string> ReasonCodes { get; set; } = new();
    public List<ContactSubject> Subjects { get; set; } = new();

    public ContactClassification? Classification { get; set; }
    public string? ClassifiedBy { get; set; }
    public DateTime? ClassifiedAt { get; set; }

    public ContactResolution? Resolution { get; set; }
    public List<string> FollowUpWorkItemIds { get; set; } = new();

    public string? Summary { get; set; }

    public string? RecordingLink { get; set; }
    public string? RecordingPlatform { get; set; }
    public string? TranscriptDocumentId { get; set; }

    public List<ContactHistoryEntry> History { get; set; } = new();
}

public class ContactCaller
{
    public CallerType Type { get; set; } = CallerType.Member;
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? Npi { get; set; }
    public string? Relationship { get; set; }
    public bool? AorOnFile { get; set; }
}

public class ContactSubject
{
    public ContactSubjectType Type { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public string? SubjectNoteId { get; set; }
    public DateTime LinkedAt { get; set; }
    public string? LinkedBy { get; set; }

    /// <summary>Where the subject's own record page lives, or null when it has none.</summary>
    public string? OpenUrl => Type switch
    {
        ContactSubjectType.Claim         => $"/claims/{SubjectId}",
        ContactSubjectType.Authorization => $"/authorizations/{SubjectId}",
        ContactSubjectType.Appeal        => $"/appeals/{SubjectId}",
        ContactSubjectType.PaymentRun    => $"/payment-runs/{SubjectId}",
        _                                => null,
    };
}

public class ContactHistoryEntry
{
    public string Action { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public DateTime At { get; set; }
    public string By { get; set; } = string.Empty;
}

/// <summary>A contact in a list.</summary>
public class ContactListItem
{
    public string Id { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public ContactChannel Channel { get; set; }
    public ContactDirection Direction { get; set; }
    public ContactStatus Status { get; set; }
    public ContactClassification? Classification { get; set; }
    public ContactResolution? Resolution { get; set; }
    public string? MemberId { get; set; }
    public string? ProviderNpi { get; set; }
    public string? CallerName { get; set; }
    public CallerType CallerType { get; set; }
    public List<string> ReasonCodes { get; set; } = new();
    public int SubjectCount { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string? AgentName { get; set; }
}

public class ContactReasonOption
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int Order { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsComplaintIndicator { get; set; }
}

// ── Requests ──────────────────────────────────────────────────────

public class StartContactRequest
{
    public ContactChannel Channel { get; set; } = ContactChannel.Phone;
    public ContactDirection Direction { get; set; } = ContactDirection.Inbound;
    public ContactCaller Caller { get; set; } = new();
    public string? MemberId { get; set; }
    public string? MemberVerification { get; set; }
    public string? ProviderNpi { get; set; }
    public string? ProviderName { get; set; }
    public string? PhoneCallId { get; set; }
    public string? Queue { get; set; }
    public List<string> ReasonCodes { get; set; } = new();
}

public class UpdateContactRequest
{
    public long Revision { get; set; }
    public string? MemberId { get; set; }
    public string? MemberVerification { get; set; }
    public string? ProviderNpi { get; set; }
    public string? ProviderName { get; set; }
    public ContactCaller? Caller { get; set; }
    public List<string>? ReasonCodes { get; set; }
    public string? Summary { get; set; }
    public ContactClassification? Classification { get; set; }
    public string? RecordingLink { get; set; }
    public string? RecordingPlatform { get; set; }
    public string? TranscriptDocumentId { get; set; }
}

public class LinkContactSubjectRequest
{
    public long Revision { get; set; }
    public ContactSubjectType Type { get; set; }
    public string SubjectId { get; set; } = string.Empty;
    public string? Reference { get; set; }
    public string? Note { get; set; }
}

public class CloseContactRequest
{
    public long Revision { get; set; }
    public ContactResolution Resolution { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? Summary { get; set; }
    public ContactClassification? Classification { get; set; }
}

public class ReopenContactRequest
{
    public long Revision { get; set; }
    public string? Reason { get; set; }
}

// ── Enumerations, with the display text the screens use ───────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContactChannel { Phone, Ivr, Chat, Email, Portal, WalkIn, Mail }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContactDirection { Inbound, Outbound }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CallerType { Member, Representative, Provider, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContactSubjectType { Claim, Authorization, Appeal, Grievance, PaymentRun, Document }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContactClassification { Inquiry, Grievance, ExpeditedGrievance, CoverageRequest, Appeal, ExpeditedAppeal }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContactResolution { ResolvedOnCall, FollowUpCreated, Transferred, CallbackScheduled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContactStatus { Open, Closed }

/// <summary>Plain-language labels for the contact enumerations.</summary>
public static class ContactDisplay
{
    public static string Label(ContactChannel channel) => channel switch
    {
        ContactChannel.Ivr    => "IVR",
        ContactChannel.WalkIn => "Walk-in",
        _                     => channel.ToString(),
    };

    public static string Label(CallerType caller) => caller switch
    {
        CallerType.Representative => "Representative",
        _                         => caller.ToString(),
    };

    public static string Label(ContactClassification classification) => classification switch
    {
        ContactClassification.ExpeditedGrievance => "Expedited grievance",
        ContactClassification.CoverageRequest    => "Coverage request",
        ContactClassification.ExpeditedAppeal    => "Expedited appeal",
        _                                        => classification.ToString(),
    };

    public static string Label(ContactResolution resolution) => resolution switch
    {
        ContactResolution.ResolvedOnCall     => "Resolved on the call",
        ContactResolution.FollowUpCreated    => "Follow-up created",
        ContactResolution.Transferred        => "Transferred",
        ContactResolution.CallbackScheduled  => "Callback scheduled",
        _                                    => resolution.ToString(),
    };

    public static string Label(ContactSubjectType type) => type switch
    {
        ContactSubjectType.PaymentRun => "Payment run",
        _                             => type.ToString(),
    };

    /// <summary>
    /// What a classification other than an inquiry means for the plan. Shown
    /// beside the choice so a rep knows a grievance starts a clock, even
    /// though step 1 does not yet start it.
    /// </summary>
    public static string? Consequence(ContactClassification classification) => classification switch
    {
        ContactClassification.Inquiry            => null,
        ContactClassification.Grievance          => "Starts member grievance intake",
        ContactClassification.ExpeditedGrievance => "Starts expedited grievance intake; the applicable rule pack sets its deadline",
        ContactClassification.CoverageRequest    => "Opens an organization determination",
        ContactClassification.Appeal             => "Starts pre-service medical appeal intake",
        ContactClassification.ExpeditedAppeal    => "Opens an expedited appeal",
        _                                        => null,
    };

    /// <summary>Talk time as a person would say it: "4m 12s".</summary>
    public static string Duration(int? seconds) => seconds switch
    {
        null      => "—",
        < 60      => $"{seconds}s",
        _         => $"{seconds / 60}m {seconds % 60:00}s",
    };
}

public sealed class OpenContactCaseRequest
{
    public long Revision { get; set; }
    public ContactCaseIntent Intent { get; set; } = new();
}
public sealed class CreateContactFollowUpRequest
{
    public long Revision { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime? DueAt { get; set; }
}
