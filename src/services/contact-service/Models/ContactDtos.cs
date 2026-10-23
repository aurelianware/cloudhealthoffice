using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ContactService.Models;

/// <summary>
/// Starts a contact. The tenant and the rep come from the caller's token, so
/// neither appears here.
/// </summary>
public class StartContactRequest
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactChannel Channel { get; set; } = ContactChannel.Phone;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactDirection Direction { get; set; } = ContactDirection.Inbound;

    public ContactCaller Caller { get; set; } = new();

    [StringLength(50)]
    public string? MemberId { get; set; }

    [StringLength(200)]
    public string? MemberVerification { get; set; }

    [StringLength(10)]
    public string? ProviderNpi { get; set; }

    [StringLength(200)]
    public string? ProviderName { get; set; }

    /// <summary>
    /// When the conversation actually began, for a contact recorded after the
    /// fact or handed over by the phone platform. Defaults to now. A time in
    /// the future is rejected.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    [StringLength(200)]
    public string? PhoneCallId { get; set; }

    [StringLength(100)]
    public string? Queue { get; set; }

    public List<string> ReasonCodes { get; set; } = new();

    /// <summary>Optional: a plan that numbers its own contacts supplies it.</summary>
    [StringLength(50)]
    public string? ContactNumber { get; set; }
}

/// <summary>
/// Changes an open contact. Every property is optional; one left null is left
/// alone. Clearing a value is not supported in step 1 — a contact is a record
/// of a conversation, so the screen corrects by adding, not by blanking.
/// </summary>
public class UpdateContactRequest
{
    /// <summary>
    /// The revision the caller read. A stale revision is rejected rather than
    /// overwriting someone else's edit.
    /// </summary>
    [Required]
    public long Revision { get; set; }

    [StringLength(50)]
    public string? MemberId { get; set; }

    [StringLength(200)]
    public string? MemberVerification { get; set; }

    [StringLength(10)]
    public string? ProviderNpi { get; set; }

    [StringLength(200)]
    public string? ProviderName { get; set; }

    public ContactCaller? Caller { get; set; }

    /// <summary>Replaces the reason list outright; an empty list is ignored.</summary>
    public List<string>? ReasonCodes { get; set; }

    [StringLength(Contact.MaxSummaryLength)]
    public string? Summary { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactClassification? Classification { get; set; }

    [StringLength(1000)]
    public string? RecordingLink { get; set; }

    [StringLength(100)]
    public string? RecordingPlatform { get; set; }

    [StringLength(100)]
    public string? TranscriptDocumentId { get; set; }
}

/// <summary>Links something the contact was about.</summary>
public class LinkSubjectRequest
{
    [Required]
    public long Revision { get; set; }

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactSubjectType Type { get; set; }

    [Required]
    [StringLength(100)]
    public string SubjectId { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Reference { get; set; }

    [StringLength(1000)]
    public string? Note { get; set; }
}

/// <summary>
/// Closes a contact: the caller has hung up and the after-call work is done.
/// </summary>
public class CloseContactRequest
{
    [Required]
    public long Revision { get; set; }

    [Required]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactResolution Resolution { get; set; }

    /// <summary>When the caller hung up. Defaults to now.</summary>
    public DateTime? EndedAt { get; set; }

    [StringLength(Contact.MaxSummaryLength)]
    public string? Summary { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactClassification? Classification { get; set; }
}

/// <summary>
/// A contact in a list: enough for the rep's recent contacts, the member's
/// contact history and the "who called about this claim" panel, without the
/// summary text or the conversation's detail.
/// </summary>
public class ContactSummary
{
    public string Id { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactChannel Channel { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactDirection Direction { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactStatus Status { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactClassification? Classification { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ContactResolution? Resolution { get; set; }

    public string? MemberId { get; set; }
    public string? ProviderNpi { get; set; }
    public string? CallerName { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CallerType CallerType { get; set; }

    public List<string> ReasonCodes { get; set; } = new();
    public int SubjectCount { get; set; }

    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? DurationSeconds { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public string? AgentName { get; set; }
}

/// <summary>Creates or changes a reason in the tenant's taxonomy.</summary>
public class SaveContactReasonRequest
{
    [Required]
    [StringLength(50)]
    public string Code { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string Label { get; set; } = string.Empty;

    [StringLength(100)]
    public string? Category { get; set; }

    public int Order { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsComplaintIndicator { get; set; }
}
