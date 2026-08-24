using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Shared webhook/poll ingress for 835: discover → retrieve → processor.
/// </summary>
public sealed class RemittanceIngress : IRemittanceIngress
{
    private static readonly HashSet<string> TransientCategories = new()
    {
        nameof(GatewayErrorCategory.RateLimited),
        nameof(GatewayErrorCategory.Timeout),
        nameof(GatewayErrorCategory.ServiceUnavailable),
        nameof(GatewayErrorCategory.Connectivity)
    };

    private readonly IHealthcareGatewayResolver _resolver;
    private readonly IRemittanceProcessor _processor;
    private readonly ILogger<RemittanceIngress> _logger;

    public RemittanceIngress(
        IHealthcareGatewayResolver resolver,
        IRemittanceProcessor processor,
        ILogger<RemittanceIngress> logger)
    {
        _resolver = resolver;
        _processor = processor;
        _logger = logger;
    }

    public async Task<RemittanceIngestResult> IngestDiscoveredAsync(
        ClaimAcknowledgmentDiscovery discovery,
        CancellationToken cancellationToken = default)
    {
        if (!IsInbound835(discovery, out var ignoreReason))
        {
            return RemittanceIngestResult.Ignore(ignoreReason);
        }

        if (string.IsNullOrWhiteSpace(discovery.ExternalAcknowledgmentId))
        {
            return new RemittanceIngestResult
            {
                Processed = true,
                Status = RemittanceLifecycleStatus.Failed,
                ErrorCategory = GatewayErrorCategory.MalformedResponse,
                ErrorMessage = "Discovered remittance is missing an external id."
            };
        }

        IRemittanceGateway gateway;
        try
        {
            gateway = _resolver.ResolveCapability<IRemittanceGateway>(
                string.IsNullOrWhiteSpace(discovery.GatewayName) ? null : discovery.GatewayName);
        }
        catch (GatewayCapabilityNotSupportedException ex)
        {
            return new RemittanceIngestResult
            {
                ErrorCategory = GatewayErrorCategory.NotSupported,
                ErrorMessage = ex.Message
            };
        }
        catch (InvalidOperationException ex)
        {
            return new RemittanceIngestResult
            {
                ErrorCategory = GatewayErrorCategory.Configuration,
                ErrorMessage = ex.Message
            };
        }

        var retrieved = await gateway.RetrieveRemittanceAsync(
            new RemittanceRetrievalRequest
            {
                ExternalRemittanceId = discovery.ExternalAcknowledgmentId,
                EventId = discovery.EventId,
                CorrelationId = discovery.CorrelationId
            },
            cancellationToken).ConfigureAwait(false);

        if (!retrieved.IsSuccess || retrieved.Result is null)
        {
            var category = retrieved.Metadata.ErrorCategory;
            if (IsTransient(category))
            {
                return RemittanceIngestResult.Transient(
                    category, retrieved.ErrorMessage ?? "Transient remittance retrieval failure.");
            }

            _logger.LogWarning(
                "Remittance retrieve failed gateway={Gateway} id={RemittanceId} category={Category}",
                Sanitize(discovery.GatewayName),
                Sanitize(discovery.ExternalAcknowledgmentId),
                category);

            var quarantined = await _processor.ProcessAsync(
                new GatewayRemittance
                {
                    RemittanceId = discovery.ExternalAcknowledgmentId.Trim(),
                    Gateway = string.IsNullOrWhiteSpace(discovery.GatewayName)
                        ? "unknown"
                        : discovery.GatewayName,
                    EventId = discovery.EventId,
                    CorrelationId = discovery.CorrelationId,
                    ReceivedAt = DateTimeOffset.UtcNow,
                    ExternalTransactionId = discovery.ExternalAcknowledgmentId,
                    RawSourceReference = discovery.ExternalAcknowledgmentId,
                    ErrorCategory = category,
                    ErrorMessage = retrieved.ErrorMessage
                },
                cancellationToken).ConfigureAwait(false);

            return new RemittanceIngestResult
            {
                Processed = true,
                Replay = quarantined.Replay,
                Status = quarantined.Status,
                RemittanceId = quarantined.RemittanceId,
                TenantId = quarantined.TenantId,
                ErrorCategory = category,
                ErrorMessage = retrieved.ErrorMessage
            };
        }

        var canonical = retrieved.Result;
        canonical.EventId ??= discovery.EventId;
        canonical.CorrelationId ??= discovery.CorrelationId;

        var processed = await _processor.ProcessAsync(canonical, cancellationToken).ConfigureAwait(false);
        return new RemittanceIngestResult
        {
            Processed = true,
            Replay = processed.Replay,
            Status = processed.Status,
            RemittanceId = processed.RemittanceId,
            TenantId = processed.TenantId,
            ErrorCategory = processed.ErrorCategory,
            ErrorMessage = processed.ErrorMessage
        };
    }

    public static bool IsInbound835(ClaimAcknowledgmentDiscovery discovery, out string? reason)
    {
        var tx = discovery.TransactionSetIdentifier?.Trim();
        if (!string.Equals(tx, "835", StringComparison.OrdinalIgnoreCase))
        {
            reason = "unsupported-transaction-set";
            return false;
        }

        var direction = discovery.Direction?.Trim();
        if (string.IsNullOrEmpty(direction) ||
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

    private static string? Sanitize(string? value) => ClaimAttachmentRules.SanitizeForLog(value);
}
