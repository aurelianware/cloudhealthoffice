using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Observability;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

internal sealed record ClaimStatusTransportResult(
    bool TransportSuccess,
    ClaimStatusResponse? Response,
    int RetryCount,
    GatewayErrorCategory Category,
    string? ErrorMessage,
    string? ExternalTransactionId);

internal delegate Task<ClaimStatusTransportResult> ClaimStatusTransport(
    ClaimStatusRequest request,
    CancellationToken ct);

/// <summary>
/// Shared 276/277 pipeline: resolve the original transmission, reuse 277CA
/// payer control numbers, persist inquiry snapshots, and keep 277CA /
/// adjudication / payment state untouched. Vendor transport is injected.
/// </summary>
internal sealed class ClaimStatusCoordinator
{
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimAcknowledgmentStore _acknowledgments;
    private readonly IClaimStatusInquiryStore _inquiries;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public ClaimStatusCoordinator(
        IClaimTransmissionStore transmissions,
        IClaimAcknowledgmentStore acknowledgments,
        IClaimStatusInquiryStore inquiries,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _transmissions = transmissions;
        _acknowledgments = acknowledgments;
        _inquiries = inquiries;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<GatewayResponse<ClaimStatusResponse>> InquireAsync(
        string gatewayName,
        ClaimStatusRequest request,
        ClaimStatusTransport transport,
        CancellationToken ct)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();

        var resolved = await ResolveTransmissionAsync(request, ct).ConfigureAwait(false);
        if (resolved.Error is { } resolveError)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0, resolveError.Category, resolveError.Message);
        }

        var transmission = resolved.Transmission;
        if (transmission is not null)
        {
            ClaimStatusRules.ApplyToRequest(request, transmission);
            var payerCcn = await ResolvePayerClaimControlNumberAsync(request, transmission, ct)
                .ConfigureAwait(false);
            request.PayerClaimControlNumber = ClaimStatusRules.FirstNonBlank(
                request.PayerClaimControlNumber, payerCcn);
        }

        var validation = ClaimStatusRules.Validate(request, transmission);
        if (validation is not null)
        {
            return Fail(gatewayName, request, startedAt, stopwatch, 0, validation.Value.Category, validation.Value.Message);
        }

        ClaimStatusTransportResult transportResult;
        try
        {
            transportResult = await transport(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in claim status inquiry for tenant {TenantId}",
                Sanitize(request.TenantId));
            return Fail(gatewayName, request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Internal, "Unexpected error executing the claim status inquiry.");
        }

        if (!transportResult.TransportSuccess || transportResult.Response is null)
        {
            await PersistAsync(
                gatewayName, request, transmission, startedAt, transportResult,
                response: null, ct).ConfigureAwait(false);
            return Fail(
                gatewayName, request, startedAt, stopwatch,
                transportResult.RetryCount, transportResult.Category,
                transportResult.ErrorMessage ?? "Claim status inquiry failed.");
        }

        var canonical = transportResult.Response;
        canonical.ClaimId ??= request.ClaimId;
        canonical.TransmissionId ??= request.TransmissionId;
        canonical.PatientControlNumber ??= request.PatientControlNumber;
        canonical.PayerClaimControlNumber ??= request.PayerClaimControlNumber;
        canonical.ExternalTransactionId ??= transportResult.ExternalTransactionId;

        var persisted = await PersistAsync(
            gatewayName, request, transmission, startedAt, transportResult,
            canonical, ct).ConfigureAwait(false);
        if (persisted.Replay)
        {
            canonical = persisted.Record.Response ?? canonical;
            canonical.ReplayOfExistingInquiry = true;
            canonical.InquiryId = persisted.Record.InquiryId;
        }
        else
        {
            canonical.InquiryId = persisted.Record.InquiryId;
        }

        var txStatus = transportResult.Category == GatewayErrorCategory.PayerRejected
            ? GatewayTransactionStatus.Rejected
            : GatewayTransactionStatus.Completed;
        var metadata = Metadata(
            gatewayName, request, startedAt, GetElapsed(stopwatch), txStatus,
            transportResult.Category, transportResult.RetryCount, canonical.ExternalTransactionId);
        Log(metadata, canonical.Status);
        RecordMetric(gatewayName, canonical.Status, transportResult.Category, metadata.Latency);
        return GatewayResponse<ClaimStatusResponse>.Success(canonical, metadata);
    }

    private async Task<(ClaimTransmissionRecord? Transmission, (GatewayErrorCategory Category, string Message)? Error)>
        ResolveTransmissionAsync(ClaimStatusRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.TransmissionId))
        {
            var byId = await _transmissions.GetByIdAsync(request.TransmissionId, ct).ConfigureAwait(false);
            if (byId is null)
            {
                return (null, (GatewayErrorCategory.TransmissionNotFound, "Claim transmission was not found."));
            }

            return (byId, null);
        }

        if (!string.IsNullOrWhiteSpace(request.ClaimId))
        {
            if (string.IsNullOrWhiteSpace(request.TenantId))
            {
                return (null, (GatewayErrorCategory.Validation, "TenantId is required when resolving by ClaimId."));
            }

            var matches = await _transmissions
                .FindByTenantAndClaimIdAsync(request.TenantId, request.ClaimId, ct)
                .ConfigureAwait(false);
            if (matches.Count == 0)
            {
                return (null, (GatewayErrorCategory.ClaimNotFound, "No claim transmission was found for this ClaimId."));
            }

            var selected = matches
                .OrderByDescending(m => m.SubmittedAtUtc)
                .First();
            return (selected, null);
        }

        return (null, null);
    }

    private async Task<string?> ResolvePayerClaimControlNumberAsync(
        ClaimStatusRequest request,
        ClaimTransmissionRecord transmission,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.PayerClaimControlNumber) ||
            !string.IsNullOrWhiteSpace(transmission.PayerClaimControlNumber))
        {
            return ClaimStatusRules.FirstNonBlank(
                request.PayerClaimControlNumber, transmission.PayerClaimControlNumber);
        }

        var acks = await _acknowledgments
            .ListByTransmissionIdAsync(transmission.TransmissionId, ct)
            .ConfigureAwait(false);
        return acks
            .OrderByDescending(a => a.ReceivedAtUtc)
            .Select(a => a.ClaimControlNumber)
            .FirstOrDefault(ccn => !string.IsNullOrWhiteSpace(ccn));
    }

    private async Task<(bool Replay, ClaimStatusInquiryRecord Record)> PersistAsync(
        string gatewayName,
        ClaimStatusRequest request,
        ClaimTransmissionRecord? transmission,
        DateTimeOffset startedAt,
        ClaimStatusTransportResult transport,
        ClaimStatusResponse? response,
        CancellationToken ct)
    {
        var record = new ClaimStatusInquiryRecord
        {
            TenantId = request.TenantId,
            ClaimId = request.ClaimId ?? transmission?.ClaimId,
            TransmissionId = request.TransmissionId ?? transmission?.TransmissionId,
            GatewayName = gatewayName,
            PayerId = request.PayerId ?? transmission?.PayerId,
            RequestedAtUtc = startedAt,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            NormalizedStatus = response?.Status ?? GatewayClaimStatus.Unknown,
            StatusCategoryCode = response?.StatusCategoryCode,
            StatusCode = response?.StatusCode,
            StatusDate = response?.StatusDate ?? response?.EffectiveDate,
            PayerClaimControlNumber = response?.PayerClaimControlNumber ?? request.PayerClaimControlNumber,
            PatientControlNumber = response?.PatientControlNumber ?? request.PatientControlNumber,
            ExternalTransactionId = response?.ExternalTransactionId ?? transport.ExternalTransactionId,
            CorrelationId = request.CorrelationId,
            RetryCount = transport.RetryCount,
            ErrorCategory = transport.Category,
            ErrorMessage = transport.ErrorMessage,
            ServiceLineNumber = request.ServiceLineNumber,
            Response = response
        };

        if (!string.IsNullOrWhiteSpace(record.ExternalTransactionId))
        {
            var existing = await _inquiries
                .GetByExternalTransactionIdAsync(record.TenantId, gatewayName, record.ExternalTransactionId, ct)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                return (true, existing);
            }
        }

        var (created, stored) = await _inquiries.TryCreateAsync(record, ct).ConfigureAwait(false);
        return (!created, stored);
    }

    private GatewayResponse<ClaimStatusResponse> Fail(
        string gatewayName,
        ClaimStatusRequest request,
        DateTimeOffset startedAt,
        long stopwatchStart,
        int retryCount,
        GatewayErrorCategory category,
        string message)
    {
        var status = category switch
        {
            GatewayErrorCategory.Timeout => GatewayTransactionStatus.TimedOut,
            GatewayErrorCategory.PayerRejected => GatewayTransactionStatus.Rejected,
            _ => GatewayTransactionStatus.Failed
        };
        var metadata = Metadata(
            gatewayName, request, startedAt, GetElapsed(stopwatchStart), status, category, retryCount, null);
        Log(metadata, status: null);
        RecordMetric(gatewayName, GatewayClaimStatus.Unknown, category, metadata.Latency);
        return GatewayResponse<ClaimStatusResponse>.Failure(message, metadata);
    }

    private static GatewayTransactionMetadata Metadata(
        string gatewayName,
        ClaimStatusRequest request,
        DateTimeOffset startedAt,
        TimeSpan latency,
        GatewayTransactionStatus status,
        GatewayErrorCategory category,
        int retryCount,
        string? externalTransactionId) =>
        new()
        {
            GatewayName = gatewayName,
            TransactionType = HealthcareTransactionType.ClaimStatus276277,
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

    private void Log(GatewayTransactionMetadata metadata, GatewayClaimStatus? status) =>
        _logger.LogInformation(
            "Gateway transaction {Gateway} {TransactionType} tenant={TenantId} status={Status} " +
            "claimStatus={ClaimStatus} category={ErrorCategory} correlation={CorrelationId} " +
            "latencyMs={LatencyMs} retries={RetryCount} extId={ExternalTransactionId}",
            metadata.GatewayName,
            metadata.TransactionType,
            Sanitize(metadata.TenantId),
            metadata.Status,
            status,
            metadata.ErrorCategory,
            Sanitize(metadata.CorrelationId),
            metadata.Latency.TotalMilliseconds,
            metadata.RetryCount,
            Sanitize(metadata.ExternalTransactionId));

    private static void RecordMetric(
        string gatewayName,
        GatewayClaimStatus status,
        GatewayErrorCategory category,
        TimeSpan latency)
    {
        ChoMetrics.ClaimStatusInquiries.Add(1,
            new KeyValuePair<string, object?>("cho.gateway", gatewayName),
            new KeyValuePair<string, object?>("cho.status", status.ToString()),
            new KeyValuePair<string, object?>("cho.error_category", category.ToString()));
        ChoMetrics.ClaimStatusDuration.Record(latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.gateway", gatewayName),
            new KeyValuePair<string, object?>("cho.status", status.ToString()));
    }

    private static string? Sanitize(string? value) => ClaimAttachmentRules.SanitizeForLog(value);

    private TimeSpan GetElapsed(long start) => Stopwatch.GetElapsedTime(start);
}
