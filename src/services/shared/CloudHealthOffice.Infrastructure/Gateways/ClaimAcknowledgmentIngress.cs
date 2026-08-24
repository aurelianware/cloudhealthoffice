using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Shared webhook/poll ingress: discover → retrieve → canonical processor.
/// </summary>
public sealed class ClaimAcknowledgmentIngress : IClaimAcknowledgmentIngress
{
    private static readonly HashSet<string> TransientCategories = new()
    {
        nameof(GatewayErrorCategory.RateLimited),
        nameof(GatewayErrorCategory.Timeout),
        nameof(GatewayErrorCategory.ServiceUnavailable),
        nameof(GatewayErrorCategory.Connectivity)
    };

    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IClaimAcknowledgmentProcessor _processor;
    private readonly ILogger<ClaimAcknowledgmentIngress> _logger;

    public ClaimAcknowledgmentIngress(
        IHealthcareGatewayResolver resolver,
        IClaimAcknowledgmentProcessor processor,
        ILogger<ClaimAcknowledgmentIngress> logger)
    {
        _resolver = resolver;
        _processor = processor;
        _logger = logger;
    }

    public async Task<ClaimAcknowledgmentIngestResult> IngestDiscoveredAsync(
        ClaimAcknowledgmentDiscovery discovery,
        CancellationToken cancellationToken = default)
    {
        if (!IsInbound277(discovery, out var ignoreReason))
        {
            return ClaimAcknowledgmentIngestResult.Ignore(ignoreReason);
        }

        if (string.IsNullOrWhiteSpace(discovery.ExternalAcknowledgmentId))
        {
            return new ClaimAcknowledgmentIngestResult
            {
                Processed = true,
                Status = ClaimAcknowledgmentStatus.Malformed,
                ErrorCategory = GatewayErrorCategory.MalformedResponse,
                ErrorMessage = "Discovered acknowledgment is missing an external id."
            };
        }

        IClaimAcknowledgmentGateway gateway;
        try
        {
            gateway = _resolver.ResolveCapability<IClaimAcknowledgmentGateway>(
                string.IsNullOrWhiteSpace(discovery.GatewayName) ? null : discovery.GatewayName);
        }
        catch (GatewayCapabilityNotSupportedException ex)
        {
            return new ClaimAcknowledgmentIngestResult
            {
                ErrorCategory = GatewayErrorCategory.NotSupported,
                ErrorMessage = ex.Message
            };
        }
        catch (InvalidOperationException ex)
        {
            return new ClaimAcknowledgmentIngestResult
            {
                ErrorCategory = GatewayErrorCategory.Configuration,
                ErrorMessage = ex.Message
            };
        }

        var retrieved = await gateway.RetrieveAcknowledgmentAsync(
            new ClaimAcknowledgmentRetrievalRequest
            {
                ExternalAcknowledgmentId = discovery.ExternalAcknowledgmentId,
                EventId = discovery.EventId,
                CorrelationId = discovery.CorrelationId
            },
            cancellationToken).ConfigureAwait(false);

        if (!retrieved.IsSuccess || retrieved.Result is null)
        {
            var category = retrieved.Metadata.ErrorCategory;
            if (IsTransient(category))
            {
                return ClaimAcknowledgmentIngestResult.Transient(
                    category, retrieved.ErrorMessage ?? "Transient acknowledgment retrieval failure.");
            }

            _logger.LogWarning(
                "Claim acknowledgment retrieve failed gateway={Gateway} ack={AckId} category={Category}",
                Sanitize(discovery.GatewayName),
                Sanitize(discovery.ExternalAcknowledgmentId),
                category);

            return new ClaimAcknowledgmentIngestResult
            {
                Processed = false,
                ErrorCategory = category,
                ErrorMessage = retrieved.ErrorMessage
            };
        }

        var canonical = retrieved.Result;
        canonical.EventId ??= discovery.EventId;
        canonical.CorrelationId ??= discovery.CorrelationId;

        var processed = await _processor.ProcessAsync(canonical, cancellationToken).ConfigureAwait(false);
        return new ClaimAcknowledgmentIngestResult
        {
            Processed = true,
            Replay = processed.Replay,
            Status = processed.Status,
            AcknowledgmentId = processed.AcknowledgmentId,
            TransmissionId = processed.TransmissionId,
            TenantId = processed.TenantId,
            ErrorCategory = processed.ErrorCategory,
            ErrorMessage = processed.ErrorMessage
        };
    }

    internal static bool IsInbound277(ClaimAcknowledgmentDiscovery discovery, out string? reason)
    {
        var tx = discovery.TransactionSetIdentifier?.Trim();
        if (!string.IsNullOrEmpty(tx) &&
            !string.Equals(tx, "277", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(tx, "277CA", StringComparison.OrdinalIgnoreCase))
        {
            reason = "unsupported-transaction-set";
            return false;
        }

        var direction = discovery.Direction?.Trim();
        if (!string.IsNullOrEmpty(direction) &&
            !string.Equals(direction, "INBOUND", StringComparison.OrdinalIgnoreCase))
        {
            reason = "not-inbound";
            return false;
        }

        reason = null;
        return true;
    }

    private static bool IsTransient(GatewayErrorCategory category) =>
        TransientCategories.Contains(category.ToString());

    private static string? Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
