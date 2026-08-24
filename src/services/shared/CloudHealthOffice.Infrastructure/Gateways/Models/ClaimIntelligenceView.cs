using System.Text.Json.Serialization;

namespace CloudHealthOffice.Infrastructure.Gateways.Models;

/// <summary>
/// Vendor-neutral claim intelligence read model. Composes 837, 277CA, 276/277,
/// 275, and 835 records that already exist. Not a system of record, not
/// adjudication, and not payment posting.
/// </summary>
public sealed class ClaimIntelligenceView
{
    public string ClaimId { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public ClaimIntelligenceIdentifiers Identifiers { get; set; } = new();

    public ClaimIntelligenceParty? Patient { get; set; }

    public ClaimIntelligenceParty? Provider { get; set; }

    public ClaimIntelligencePayer? Payer { get; set; }

    public ClaimIntelligenceLifecycleStatus LifecycleStatus { get; set; } =
        ClaimIntelligenceLifecycleStatus.Unknown;

    public ClaimIntelligenceTransactionSet Transactions { get; set; } = new();

    public ClaimIntelligenceFinancialSummary Financial { get; set; } = new();

    public ClaimIntelligenceAttachmentSummary Attachments { get; set; } = new();

    public List<ClaimIntelligenceTimelineEvent> Timeline { get; set; } = new();

    public ClaimIntelligenceWorkflow Workflow { get; set; } = new();

    public ClaimIntelligenceSignals Signals { get; set; } = new();

    public DateTimeOffset GeneratedAtUtc { get; set; }
}

public sealed class ClaimIntelligenceIdentifiers
{
    public string? TransmissionId { get; set; }

    public string? PatientControlNumber { get; set; }

    public string? PayerClaimControlNumber { get; set; }

    public string? SubmissionId { get; set; }

    public string? GatewayName { get; set; }
}

/// <summary>Limited party projection for authorized callers. Never written to logs.</summary>
public sealed class ClaimIntelligenceParty
{
    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? OrganizationName { get; set; }

    public string? Npi { get; set; }

    public string? MemberId { get; set; }
}

public sealed class ClaimIntelligencePayer
{
    public string? PayerId { get; set; }

    public string? Name { get; set; }
}

public sealed class ClaimIntelligenceTransactionSet
{
    [JsonPropertyName("837")]
    public ClaimIntelligenceTransactionSnapshot? Submission { get; set; }

    [JsonPropertyName("277CA")]
    public ClaimIntelligenceTransactionSnapshot? Acknowledgment { get; set; }

    [JsonPropertyName("276277")]
    public ClaimIntelligenceTransactionSnapshot? Status { get; set; }

    [JsonPropertyName("275")]
    public ClaimIntelligenceTransactionSnapshot? Attachments { get; set; }

    [JsonPropertyName("835")]
    public ClaimIntelligenceTransactionSnapshot? Remittance { get; set; }
}

public sealed class ClaimIntelligenceTransactionSnapshot
{
    public string Status { get; set; } = string.Empty;

    public string? RecordId { get; set; }

    public DateTimeOffset? AtUtc { get; set; }

    public string? SourceTransaction { get; set; }
}

public sealed class ClaimIntelligenceFinancialSummary
{
    public decimal? SubmittedAmount { get; set; }

    public decimal? AllowedAmount { get; set; }

    public decimal? PaidAmount { get; set; }

    public decimal? PatientResponsibility { get; set; }

    public bool HasRemittance { get; set; }
}

public sealed class ClaimIntelligenceAttachmentSummary
{
    public bool Requested { get; set; }

    public bool Received { get; set; }

    public bool AttachmentAvailable { get; set; }

    public int Count { get; set; }

    public int OutboundCount { get; set; }

    public int InboundCount { get; set; }

    public List<string> Types { get; set; } = new();
}

public sealed class ClaimIntelligenceTimelineEvent
{
    public string EventId { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string SourceTransaction { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? Metadata { get; set; }
}

public sealed class ClaimIntelligenceWorkflow
{
    public string? ProcedureSummary { get; set; }

    public string? PayerDisplay { get; set; }

    public DateOnly? SubmittedOn { get; set; }

    public string? Expected { get; set; }

    public string? PatientResponsibilityDisplay { get; set; }

    public ClaimIntelligenceNextAction NextAction { get; set; } =
        ClaimIntelligenceNextAction.None;
}

/// <summary>Structured, non-AI signals that later assistants can consume.</summary>
public sealed class ClaimIntelligenceSignals
{
    public bool ActionRequired { get; set; }

    public bool NeedsFollowUp { get; set; }

    public bool MissingDocumentation { get; set; }

    public bool UnusualPayerResponse { get; set; }

    public List<string> MissingTransactionLinks { get; set; } = new();
}

/// <summary>
/// Business-friendly claim lifecycle. Distinct from X12 codes, 277CA
/// acknowledgment, 276/277 inquiry status, and 835 posting.
/// </summary>
public enum ClaimIntelligenceLifecycleStatus
{
    Unknown = 0,
    Draft,
    Submitted,
    AcceptedByClearinghouse,
    AcceptedByPayer,
    Processing,
    PendingInformation,
    Denied,
    Paid,
    PartiallyPaid,
    Completed
}

public enum ClaimIntelligenceNextAction
{
    None = 0,
    WaitForClearinghouse,
    WaitForPayer,
    ProvideInformation,
    CorrectAndResubmit,
    ReadyForPosting
}

public sealed class ClaimIntelligenceRequest
{
    public string TenantId { get; set; } = string.Empty;

    public string ClaimId { get; set; } = string.Empty;
}
