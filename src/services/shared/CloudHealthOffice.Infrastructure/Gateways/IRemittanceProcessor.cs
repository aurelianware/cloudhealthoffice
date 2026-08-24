using CloudHealthOffice.Infrastructure.Gateways.Models;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Vendor-neutral 835 processor. Matches remitted claims to transmissions,
/// persists the receipt, and emits identifier-only events. Does not post
/// payment, change 277CA, or overwrite 276/277 claim status.
/// </summary>
public interface IRemittanceProcessor
{
    Task<RemittanceProcessResult> ProcessAsync(
        GatewayRemittance remittance,
        CancellationToken cancellationToken = default);

    Task DispatchPendingAsync(CancellationToken cancellationToken = default);
}

public sealed class RemittanceProcessResult
{
    public bool Replay { get; init; }

    public RemittanceLifecycleStatus Status { get; init; }

    public string RemittanceId { get; init; } = string.Empty;

    public string TenantId { get; init; } = string.Empty;

    public int ClaimCount { get; init; }

    public int MatchedClaimCount { get; init; }

    public GatewayErrorCategory ErrorCategory { get; init; }

    public string? ErrorMessage { get; init; }

    public bool EventsPublished { get; init; }
}

/// <summary>
/// Ingress for a discovered 835 pointer (webhook or poll). Retrieves via
/// <see cref="Capabilities.IRemittanceGateway"/> and applies
/// <see cref="IRemittanceProcessor"/>.
/// </summary>
public interface IRemittanceIngress
{
    Task<RemittanceIngestResult> IngestDiscoveredAsync(
        ClaimAcknowledgmentDiscovery discovery,
        CancellationToken cancellationToken = default);
}

public sealed class RemittanceIngestResult
{
    public bool Ignored { get; init; }

    public bool Replay { get; init; }

    public bool TransientFailure { get; init; }

    public bool Processed { get; init; }

    public RemittanceLifecycleStatus? Status { get; init; }

    public string? RemittanceId { get; init; }

    public string? TenantId { get; init; }

    public GatewayErrorCategory ErrorCategory { get; init; }

    public string? ErrorMessage { get; init; }

    public static RemittanceIngestResult Ignore(string? reason = null) =>
        new() { Ignored = true, ErrorMessage = reason };

    public static RemittanceIngestResult Transient(
        GatewayErrorCategory category, string message) =>
        new() { TransientFailure = true, ErrorCategory = category, ErrorMessage = message };
}
