using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

public static class ClaimAttachmentEventTopics
{
    public const string TopicName = "claim-attachment-events";

    public const string MessageTypeProperty = "MessageType";
}

public static class ClaimAttachmentMessageTypes
{
    public const string Stored = "ClaimAttachmentStored";

    /// <summary>
    /// Content bytes were read from the secure store in order to submit.
    /// Distinct from <see cref="ClaimAttachmentTransmissionStatus.ReadyForSubmission"/>.
    /// </summary>
    public const string ReadForSubmission = "ClaimAttachmentReadForSubmission";

    public const string Submitted = "ClaimAttachmentSubmitted";

    public const string TransmissionResult = "ClaimAttachmentTransmissionResult";
}

/// <summary>Identifier-only attachment event. Must not carry PHI or file bytes.</summary>
public sealed class ClaimAttachmentAuditMessage
{
    public string MessageType { get; set; } = string.Empty;

    public string AttachmentId { get; set; } = string.Empty;

    public string AttachmentTransmissionId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string? ClaimId { get; set; }

    public string? ClaimTransmissionId { get; set; }

    public string? ContentType { get; set; }

    public long ContentLength { get; set; }

    public string? ChecksumPrefix { get; set; }

    public ClaimAttachmentTransmissionStatus Status { get; set; }

    public GatewayErrorCategory ErrorCategory { get; set; }

    public string? CorrelationId { get; set; }
}
