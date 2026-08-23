using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Real external eligibility gateway backed by the Stedi Healthcare real-time
/// eligibility (270/271) JSON API.
///
/// It advertises only the <see cref="GatewayCapability.Eligibility"/> capability
/// — claim submission, status, acknowledgment, attachments, and remittance stay
/// explicitly unsupported even though Stedi offers some of them, because the
/// capability surface describes what Cloud Health Office currently implements.
///
/// The gateway is pure transport + translation: it maps the canonical request to
/// Stedi's JSON, executes the call, and normalizes the payer's response back into
/// the canonical <see cref="GatewayEligibilityResponse"/>. It performs no benefit,
/// accumulator, or adjudication logic — the response is an external payer
/// eligibility statement, not a Cloud Health Office calculation.
/// </summary>
public sealed class StediHealthcareGateway : IEligibilityGateway
{
    /// <summary>The name this gateway registers under and is resolved by.</summary>
    public const string GatewayName = "Stedi";

    private static readonly IReadOnlySet<GatewayCapability> SupportedCapabilities =
        new HashSet<GatewayCapability> { GatewayCapability.Eligibility };

    private readonly StediEligibilityApiClient _apiClient;
    private readonly IStediPayerResolver _payerResolver;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger<StediHealthcareGateway> _logger;
    private readonly TimeProvider _timeProvider;

    internal StediHealthcareGateway(
        StediEligibilityApiClient apiClient,
        IStediPayerResolver payerResolver,
        IOptions<StediGatewayOptions> options,
        ILogger<StediHealthcareGateway> logger,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient;
        _payerResolver = payerResolver;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => GatewayName;

    public IReadOnlySet<GatewayCapability> Capabilities => SupportedCapabilities;

    public async Task<GatewayResponse<GatewayEligibilityResponse>> CheckEligibilityAsync(
        GatewayEligibilityRequest request, CancellationToken ct = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();

        // 1. Configuration must be valid — never silently fall back to another
        //    gateway when Stedi was explicitly selected.
        var configErrors = _options.Value.Validate();
        if (configErrors.Count > 0)
        {
            return Fail(request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Configuration,
                "Stedi gateway is not configured correctly: " + string.Join(" ", configErrors));
        }

        // 2. Request must carry tenant + subscriber.
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.SubscriberId))
        {
            return Fail(request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Validation, "TenantId and SubscriberId are required.");
        }

        // 3. Resolve the payer to a Stedi trading-partner id (tenant-scoped).
        var stediPayerId = _payerResolver.Resolve(request.TenantId, request.PayerId);
        if (string.IsNullOrWhiteSpace(stediPayerId))
        {
            return Fail(request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Validation,
                "No payer identifier could be resolved for the request (PayerId is missing and no mapping applied).");
        }

        // 4. Map, call, normalize.
        var stediRequest = StediEligibilityMapper.ToStediRequest(request, stediPayerId);

        StediApiResult apiResult;
        try
        {
            apiResult = await _apiClient.SendEligibilityAsync(stediRequest, ct).ConfigureAwait(false);
        }
        catch (StediApiException ex)
        {
            return Fail(request, startedAt, stopwatch, ex.RetryCount, ex.Category, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Stedi eligibility for tenant {TenantId}", request.TenantId);
            return Fail(request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Internal, "Unexpected error executing the Stedi eligibility request.");
        }

        var canonical = StediEligibilityMapper.ToCanonicalResponse(apiResult.Response);

        // A payer AAA rejection is a completed transport with a business
        // rejection: surface the normalized info but mark it as rejected.
        var isPayerRejection =
            canonical.CoverageStatus == GatewayCoverageStatus.Unknown &&
            !string.IsNullOrEmpty(canonical.RejectionReason);

        var status = isPayerRejection ? GatewayTransactionStatus.Rejected : GatewayTransactionStatus.Completed;
        var category = isPayerRejection ? GatewayErrorCategory.PayerRejected : GatewayErrorCategory.None;

        var metadata = BuildMetadata(
            request, startedAt, GetElapsed(stopwatch), status, category,
            apiResult.RetryCount, apiResult.ExternalTransactionId);

        Log(metadata);
        return GatewayResponse<GatewayEligibilityResponse>.Success(canonical, metadata);
    }

    private GatewayResponse<GatewayEligibilityResponse> Fail(
        GatewayEligibilityRequest request,
        DateTimeOffset startedAt,
        long stopwatchStart,
        int retryCount,
        GatewayErrorCategory category,
        string message)
    {
        var status = category switch
        {
            GatewayErrorCategory.Timeout => GatewayTransactionStatus.TimedOut,
            _ => GatewayTransactionStatus.Failed
        };

        var metadata = BuildMetadata(
            request, startedAt, GetElapsed(stopwatchStart), status, category, retryCount, externalTransactionId: null);
        Log(metadata);
        return GatewayResponse<GatewayEligibilityResponse>.Failure(message, metadata);
    }

    private GatewayTransactionMetadata BuildMetadata(
        GatewayEligibilityRequest request,
        DateTimeOffset startedAt,
        TimeSpan latency,
        GatewayTransactionStatus status,
        GatewayErrorCategory category,
        int retryCount,
        string? externalTransactionId) =>
        new()
        {
            GatewayName = GatewayName,
            TransactionType = HealthcareTransactionType.Eligibility270271,
            SubmittedAtUtc = startedAt,
            CompletedAtUtc = startedAt + latency,
            Status = status,
            ExternalTransactionId = externalTransactionId,
            CorrelationId = request.CorrelationId,
            TenantId = request.TenantId,
            Latency = latency,
            RetryCount = retryCount,
            ErrorCategory = category
        };

    // Logs ONLY non-PHI transaction metadata — never subscriber/member data or
    // any request/response body.
    private void Log(GatewayTransactionMetadata metadata) =>
        _logger.LogInformation(
            "Gateway transaction {Gateway} {TransactionType} tenant={TenantId} status={Status} " +
            "category={ErrorCategory} correlation={CorrelationId} latencyMs={LatencyMs} retries={RetryCount} extId={ExternalTransactionId}",
            metadata.GatewayName,
            metadata.TransactionType,
            metadata.TenantId,
            metadata.Status,
            metadata.ErrorCategory,
            metadata.CorrelationId,
            metadata.Latency.TotalMilliseconds,
            metadata.RetryCount,
            metadata.ExternalTransactionId);

    private TimeSpan GetElapsed(long start) => Stopwatch.GetElapsedTime(start);
}
