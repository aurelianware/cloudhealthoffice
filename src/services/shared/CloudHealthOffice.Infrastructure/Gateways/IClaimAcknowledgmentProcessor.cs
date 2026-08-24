using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Vendor-neutral 277CA processor. Matches a canonical acknowledgment to a
/// durable transmission, persists it, updates transmission lifecycle, and
/// emits identifier-only events. Transport (webhook vs poll) must not
/// duplicate this logic.
/// </summary>
public interface IClaimAcknowledgmentProcessor
{
    Task<ClaimAcknowledgmentProcessResult> ProcessAsync(
        GatewayClaimAcknowledgment acknowledgment,
        CancellationToken cancellationToken = default);

    Task DispatchPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class ClaimAcknowledgmentProcessResult
{
    public bool Replay { get; init; }

    public ClaimAcknowledgmentStatus Status { get; init; }

    public string AcknowledgmentId { get; init; } = string.Empty;

    public string? TransmissionId { get; init; }

    public string TenantId { get; init; } = string.Empty;

    public GatewayClaimTransmissionStatus? TransmissionStatus { get; init; }

    public GatewayErrorCategory ErrorCategory { get; init; }

    public string? ErrorMessage { get; init; }

    public bool EventsPublished { get; init; }
}

/// <summary>
/// Ingress for a discovered claim-response pointer (webhook or poll).
/// Retrieves via <see cref="Capabilities.IClaimAcknowledgmentGateway"/> and
/// applies <see cref="IClaimAcknowledgmentProcessor"/>.
/// </summary>
public interface IClaimAcknowledgmentIngress
{
    Task<ClaimAcknowledgmentIngestResult> IngestDiscoveredAsync(
        ClaimAcknowledgmentDiscovery discovery,
        CancellationToken cancellationToken = default);
}

public sealed class ClaimAcknowledgmentIngestResult
{
    public bool Ignored { get; init; }

    public bool Replay { get; init; }

    public bool TransientFailure { get; init; }

    public bool Processed { get; init; }

    public ClaimAcknowledgmentStatus? Status { get; init; }

    public string? AcknowledgmentId { get; init; }

    public string? TransmissionId { get; init; }

    public string? TenantId { get; init; }

    public GatewayErrorCategory ErrorCategory { get; init; }

    public string? ErrorMessage { get; init; }

    public static ClaimAcknowledgmentIngestResult Ignore(string? reason = null) =>
        new() { Ignored = true, ErrorMessage = reason };

    public static ClaimAcknowledgmentIngestResult Transient(
        GatewayErrorCategory category, string message) =>
        new() { TransientFailure = true, ErrorCategory = category, ErrorMessage = message };
}
