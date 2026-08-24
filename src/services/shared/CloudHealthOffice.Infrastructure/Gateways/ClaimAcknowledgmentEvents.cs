using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

public static class ClaimAcknowledgmentEventTopics
{
    public const string TopicName = "claim-acknowledgment-events";

    public const string MessageTypeProperty = "MessageType";
}

public static class ClaimAcknowledgmentMessageTypes
{
    public const string Received = "ClaimAcknowledgmentReceived";

    public const string Accepted = "ClaimAcknowledgmentAccepted";

    public const string Rejected = "ClaimAcknowledgmentRejected";
}

/// <summary>Identifier-only 277CA event. Must not carry PHI.</summary>
public sealed class ClaimAcknowledgmentReceivedMessage
{
    public string AcknowledgmentId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public string? TransmissionId { get; set; }

    public string? ClaimId { get; set; }

    public ClaimAcknowledgmentStatus Status { get; set; }

    public GatewayClaimTransmissionStatus? TransmissionStatus { get; set; }

    public string? CorrelationId { get; set; }

    public bool Replay { get; set; }
}
