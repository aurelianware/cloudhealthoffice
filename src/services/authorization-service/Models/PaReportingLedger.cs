using System.ComponentModel.DataAnnotations;

namespace AuthorizationService.Models;

/// <summary>Server-owned evidence embedded in the authorization's atomic Mongo document.</summary>
public sealed class PaReportingLedger
{
    public int Version { get; set; } = 1;
    public DateTime ReceivedAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public LineOfBusiness LineOfBusiness { get; set; }
    public bool Expedited { get; set; }
    public bool DrugExcluded { get; set; }
    public List<PaReportingFact> Facts { get; set; } = new();
}

public enum PaReportingFactKind { Determination = 1, AppealDecision = 2, ReviewExtension = 3 }

public sealed class PaReportingFact
{
    public string EventId { get; set; } = string.Empty;
    public PaReportingFactKind Kind { get; set; }
    public DateTime EffectiveAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public AuthorizationStatus? Outcome { get; set; }
    public DateTime? DecisionAt { get; set; }
    public DateTime? OriginalDueAt { get; set; }
    public DateTime? ExtendedDueAt { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string EvidenceReference { get; set; } = string.Empty;
    public string RecordedBy { get; set; } = string.Empty;
}

/// <summary>An attested fact from a PA source, not a command to decide care or grant an extension.</summary>
public sealed class RecordPaReportingFact
{
    [Required, StringLength(128), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    public string EventId { get; set; } = string.Empty;
    public PaReportingFactKind Kind { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public AuthorizationStatus? Outcome { get; set; }
    public DateTimeOffset? OriginalDueAt { get; set; }
    public DateTimeOffset? ExtendedDueAt { get; set; }
    [Required, StringLength(80), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    public string SourceSystem { get; set; } = string.Empty;
    /// <summary>Opaque durable source reference. Do not submit a narrative, PHI or a signed URL.</summary>
    [Required, StringLength(200), RegularExpression(@"^[A-Za-z0-9][A-Za-z0-9._:/-]*$")]
    public string EvidenceReference { get; set; } = string.Empty;
}
