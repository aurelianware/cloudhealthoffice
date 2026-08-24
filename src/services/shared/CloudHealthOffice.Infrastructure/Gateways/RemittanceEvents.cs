using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

public static class RemittanceEventTopics
{
    public const string TopicName = "claim-remittance-events";

    public const string MessageTypeProperty = "MessageType";
}

public static class RemittanceMessageTypes
{
    public const string Received = "RemittanceReceived";

    public const string Matched = "RemittanceMatched";

    public const string Unmatched = "RemittanceUnmatched";
}

/// <summary>Identifier-only remittance event. Must not carry PHI or bank data.</summary>
public sealed class RemittanceReceivedMessage
{
    public string RemittanceId { get; set; } = string.Empty;

    public string Gateway { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public RemittanceLifecycleStatus Status { get; set; }

    public int ClaimCount { get; set; }

    public int MatchedClaimCount { get; set; }

    public decimal PaymentAmount { get; set; }

    public string? CorrelationId { get; set; }

    public bool Replay { get; set; }
}
