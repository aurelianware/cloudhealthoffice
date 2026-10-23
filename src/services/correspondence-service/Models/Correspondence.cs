using MongoDB.Bson.Serialization.Attributes;

namespace CorrespondenceService.Models;

// docs/architecture/correspondence.md §5. Every outbound letter is produced
// here: a letter type says what kind of letter it is, a template (versioned,
// effective-dated, scoped) says what it looks like, and a letter is one
// rendered, frozen copy sent to one recipient.

public enum LetterCategory { Appeal, Grievance, Authorization, Claim, Member, Provider, IdCard }

public enum LetterAudience { Member, Provider, Representative }

/// <summary>
/// The four template kinds (§7). Standardized (CMS IDN, NOMNC, DENC) and
/// State-prescribed (Texas HHSC Notices 1 and 2) templates are filled, never
/// reworded: their locked blocks cannot change between versions.
/// </summary>
public enum TemplateKind { Standardized, StatePrescribed, Model, PlanAuthored }

public enum TemplateFormat { Standard, LargePrint, Braille, AccessiblePdf }

public enum TemplateStatus { Draft, Approved, Retired }

/// <summary>
/// What a block is, which decides how it is laid out and, for the two address
/// kinds, what fills it when its own text is blank:
/// <see cref="AddressBlock"/> takes the recipient's address and
/// <see cref="ReturnAddressBlock"/> the sending carrier's
/// (plan-configuration.md §12.4).
/// </summary>
public enum TemplateBlockKind { Heading, Paragraph, AddressBlock, Signature, PageBreak, ReturnAddressBlock }

public enum LetterStatus
{
    /// <summary>Rendered, checked and stored write-once in the document catalog.</summary>
    Rendered,

    /// <summary>A pre-release check failed; nothing was stored and nothing can be sent.</summary>
    CheckFailed,
}

[BsonIgnoreExtraElements]
public sealed class LetterType
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Stable code, e.g. "APPEAL_RESOLUTION_UPHELD". Unique per tenant.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public LetterCategory Category { get; set; }
    public LetterAudience Audience { get; set; }

    /// <summary>Letters of this type need a reviewer's approval before release (build step 3).</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>Document store type the rendered letter is filed as.</summary>
    public string DocumentType { get; set; } = "OutboundLetter";

    /// <summary>A significant communication under Section 1557 (taglines and notice of availability required).</summary>
    public bool IsSignificantCommunication { get; set; }

    /// <summary>Required material under 42 CFR 422.2267 (Medicare Advantage).</summary>
    public bool IsRequiredMaterial { get; set; }

    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

/// <summary>Where a template applies. An unset field matches every value; the most specific match wins.</summary>
public sealed class TemplateScope
{
    public string? LineOfBusiness { get; set; }
    public string? State { get; set; }
    public string? Product { get; set; }
    public string? PlanId { get; set; }
}

public sealed class TemplateBlock
{
    /// <summary>Stable id within the template, e.g. "appeal-rights". Required blocks and locked text are tracked by id.</summary>
    public string Id { get; set; } = string.Empty;
    public TemplateBlockKind Kind { get; set; } = TemplateBlockKind.Paragraph;

    /// <summary>Liquid text (Fluid): <c>{{ member.name }}</c>, <c>{% if %}</c>.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Prescribed wording: only its merge fields are filled, and it cannot change between versions of a Standardized or State-prescribed template.</summary>
    public bool Locked { get; set; }

    /// <summary>Font size in points; null uses the template's body size.</summary>
    public float? FontSize { get; set; }
    public bool Bold { get; set; }

    /// <summary>SHA-256 of <see cref="Text"/> recorded at approval; checked before every release.</summary>
    public string? ApprovedTextHash { get; set; }
}

public sealed class TemplateTranslation
{
    public string TranslatorId { get; set; } = string.Empty;
    public DateTime ReviewedAt { get; set; }
}

/// <summary>
/// One version of a template. <see cref="TemplateId"/> is shared by every
/// version; a letter pins the exact <see cref="Version"/> it was built from.
/// Only Draft versions can change.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class LetterTemplate
{
    /// <summary>"{TemplateId}:v{Version}".</summary>
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string LetterTypeCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TemplateKind Kind { get; set; } = TemplateKind.PlanAuthored;
    public TemplateStatus Status { get; set; } = TemplateStatus.Draft;

    public string? FormNumber { get; set; }
    public string? OmbNumber { get; set; }
    public DateOnly? FormExpiresOn { get; set; }

    public TemplateScope Scope { get; set; } = new();

    /// <summary>ISO 639-1 language, e.g. "en", "es".</summary>
    public string Language { get; set; } = "en";
    public TemplateFormat Format { get; set; } = TemplateFormat.Standard;
    public TemplateTranslation? Translation { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public List<TemplateBlock> Blocks { get; set; } = new();

    /// <summary>Block ids that must be present and render non-empty.</summary>
    public List<string> RequiredBlocks { get; set; } = new();

    /// <summary>Letter type codes whose templates are appended as attachments (e.g. a notice of availability).</summary>
    public List<string> Attachments { get; set; } = new();

    /// <summary>Body text size in points; never below <see cref="MinFontPt"/>.</summary>
    public float BodyFontPt { get; set; } = 12f;

    /// <summary>Smallest size any text may use (12 pt by default; 18 pt for large print).</summary>
    public float MinFontPt { get; set; } = 12f;

    /// <summary>Highest Flesch-Kincaid grade the unlocked text may reach; null to skip the check.</summary>
    public double? ReadingLevelMax { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredAt { get; set; }

    /// <summary>Optimistic concurrency for draft edits and status changes.</summary>
    public long Revision { get; set; }

    public static string MakeId(string templateId, int version) => $"{templateId}:v{version}";
}

/// <summary>The domain record a letter is about. Kinds match the document catalog's link kinds.</summary>
public sealed class LetterSubject
{
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
}

public sealed class LetterRecipient
{
    public LetterAudience Party { get; set; } = LetterAudience.Member;
    public string Name { get; set; } = string.Empty;
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? PostalCode { get; set; }

    /// <summary>Where the address came from (member record, representative, confidential-communications instruction, request).</summary>
    public string AddressSource { get; set; } = "Request";
    public DateTime? AddressVerifiedAt { get; set; }
}

public sealed class LetterTrigger
{
    /// <summary>"Manual", or the domain event (e.g. "AppealDecided").</summary>
    public string Event { get; set; } = "Manual";
    public string? SourceId { get; set; }
    public string? RequestedBy { get; set; }
    public string? Reason { get; set; }
}

public sealed class LetterCheck
{
    public string Name { get; set; } = string.Empty;
    public bool Passed { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Append-only history of a letter (§5 LetterEvent).</summary>
public sealed class LetterEvent
{
    public string Type { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

[BsonIgnoreExtraElements]
public sealed class Letter
{
    [BsonId]
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string LetterTypeCode { get; set; } = string.Empty;

    /// <summary>The template version this letter was built from, pinned forever.</summary>
    public string TemplateId { get; set; } = string.Empty;
    public int TemplateVersion { get; set; }
    public string Language { get; set; } = "en";
    public TemplateFormat Format { get; set; }

    public List<LetterSubject> Subjects { get; set; } = new();
    public LetterRecipient Recipient { get; set; } = new();
    public LetterTrigger Trigger { get; set; } = new();

    /// <summary>The rule pack notice this letter satisfies, when there is one.</summary>
    public string? RuleRef { get; set; }
    public DateTime? DueBy { get; set; }

    /// <summary>The date printed on the letter (the channel's planned mail date once channels exist).</summary>
    public DateOnly LetterDate { get; set; }

    /// <summary>The merge data and recipient exactly as rendered (canonical JSON), and its SHA-256.</summary>
    public string DataSnapshot { get; set; } = string.Empty;
    public string DataSnapshotHash { get; set; } = string.Empty;

    /// <summary>The rendered PDF in the document catalog (write-once). Null when a check failed.</summary>
    public string? DocumentId { get; set; }
    public string? DocumentSha256 { get; set; }
    public int PageCount { get; set; }

    public LetterStatus Status { get; set; }
    public List<LetterCheck> Checks { get; set; } = new();
    public List<LetterEvent> Events { get; set; } = new();

    public DateTime CreatedAt { get; set; }
}
