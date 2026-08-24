using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Gateways.Stedi.Mapping;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Real external eligibility gateway backed by the Stedi Healthcare real-time
/// eligibility (270/271) JSON API.
///
/// It advertises eligibility, claim submission, 277CA acknowledgment, and
/// 275 claim attachments. Claim status and remittance stay explicitly
/// unsupported even though Stedi offers some of them.
///
/// The gateway is pure transport + translation: it maps the canonical request to
/// Stedi's JSON, executes the call, and normalizes the payer's response back into
/// the canonical <see cref="GatewayEligibilityResponse"/>. It performs no benefit,
/// accumulator, or adjudication logic — the response is an external payer
/// eligibility statement, not a Cloud Health Office calculation.
/// </summary>
public sealed class StediHealthcareGateway : IEligibilityGateway, IClaimSubmissionGateway, IClaimAcknowledgmentGateway, IClaimAttachmentGateway
{
    /// <summary>The name this gateway registers under and is resolved by.</summary>
    public const string GatewayName = "Stedi";

    private static readonly IReadOnlySet<GatewayCapability> SupportedCapabilities =
        new HashSet<GatewayCapability>
        {
            GatewayCapability.Eligibility,
            GatewayCapability.ClaimSubmission,
            GatewayCapability.ClaimAcknowledgment,
            GatewayCapability.ClaimAttachment
        };

    private readonly StediEligibilityApiClient _apiClient;
    private readonly StediClaimApiClient? _claimClient;
    private readonly StediClaimAcknowledgmentApiClient? _acknowledgmentClient;
    private readonly StediClaimAttachmentApiClient? _attachmentClient;
    private readonly IStediPayerResolver _payerResolver;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly ClaimAttachmentCoordinator _attachments;
    private readonly IOptions<StediGatewayOptions> _options;
    private readonly ILogger<StediHealthcareGateway> _logger;
    private readonly TimeProvider _timeProvider;

    internal StediHealthcareGateway(
        StediEligibilityApiClient apiClient,
        IStediPayerResolver payerResolver,
        IOptions<StediGatewayOptions> options,
        ILogger<StediHealthcareGateway> logger,
        TimeProvider? timeProvider = null,
        StediClaimApiClient? claimClient = null,
        IClaimTransmissionStore? transmissions = null,
        StediClaimAcknowledgmentApiClient? acknowledgmentClient = null,
        StediClaimAttachmentApiClient? attachmentClient = null,
        IClaimAttachmentTransmissionStore? attachmentStore = null,
        IClaimAttachmentContentStore? content = null,
        IOptions<HealthcareTransactionOptions>? transactionOptions = null,
        CloudHealthOffice.Infrastructure.Messaging.IMessageBus? messageBus = null)
    {
        _apiClient = apiClient;
        _claimClient = claimClient;
        _acknowledgmentClient = acknowledgmentClient;
        _attachmentClient = attachmentClient;
        _payerResolver = payerResolver;
        _transmissions = transmissions ?? new InMemoryClaimTransmissionStore();
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var attachmentOptions = transactionOptions?.Value.ClaimAttachments ?? new ClaimAttachmentOptions();
        _attachments = new ClaimAttachmentCoordinator(
            _transmissions,
            attachmentStore ?? new InMemoryClaimAttachmentTransmissionStore(),
            content ?? new InMemoryClaimAttachmentContentStore(attachmentOptions),
            attachmentOptions,
            logger,
            _timeProvider,
            messageBus);
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
        if (string.IsNullOrWhiteSpace(request.TenantId) ||
            string.IsNullOrWhiteSpace(request.ResolveSubscriberMemberId()))
        {
            return Fail(request, startedAt, stopwatch, 0,
                GatewayErrorCategory.Validation, "TenantId and SubscriberId are required.");
        }

        // 3. Resolve the payer through the canonical payer reference service
        //    (tenant-scoped). Arbitrary payer ids are never passed through.
        var resolution = await _payerResolver
            .ResolveAsync(request.TenantId, request.PayerId, ct)
            .ConfigureAwait(false);
        if (resolution.Status != PayerResolutionStatus.Found ||
            string.IsNullOrWhiteSpace(resolution.ExternalIdentifierValue))
        {
            return Fail(request, startedAt, stopwatch, 0,
                MapResolution(resolution.Status),
                resolution.Message ?? "Payer could not be resolved for this request.");
        }

        var stediPayerId = resolution.ExternalIdentifierValue;

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
            _logger.LogError(ex, "Unexpected error in Stedi eligibility for tenant {TenantId}",
                SanitizeForLog(request.TenantId));
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

    public async Task<GatewayResponse<GatewayClaimSubmissionResult>> SubmitClaimAsync(
        GatewayClaimSubmissionRequest request, CancellationToken ct = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();
        var key = request.ResolveIdempotencyKey();

        var configErrors = _options.Value.Validate();
        if (configErrors.Count > 0)
        {
            return ClaimFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Configuration,
                "Stedi gateway is not configured correctly: " + string.Join(" ", configErrors));
        }

        if (_claimClient is null)
        {
            return ClaimFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Configuration,
                "Stedi claim submission client is not registered.");
        }

        var validation = GatewayClaimSubmissionValidator.Validate(request);
        if (validation is not null)
        {
            var category = validation.Contains("TypeOfBill", StringComparison.Ordinal)
                || validation.Contains("revenue code", StringComparison.OrdinalIgnoreCase)
                ? GatewayErrorCategory.ClaimTypeNotReady
                : GatewayErrorCategory.Validation;
            return ClaimFail(request, startedAt, stopwatch, 0, category, validation);
        }

        var existing = await _transmissions.GetByIdempotencyKeyAsync(request.TenantId, key, ct)
            .ConfigureAwait(false);
        if (existing is not null &&
            GatewayClaimTransmissionStatuses.PreventsDuplicateSubmit(existing.Status))
        {
            RecordClaimMetric(request, existing.Status, GatewayErrorCategory.None, GetElapsed(stopwatch));
            var replayMeta = ClaimMetadata(request, startedAt, GetElapsed(stopwatch),
                GatewayTransactionStatus.Completed, GatewayErrorCategory.None, existing.RetryCount,
                existing.ExternalTransactionId);
            Log(replayMeta);
            return GatewayResponse<GatewayClaimSubmissionResult>.Success(
                new GatewayClaimSubmissionResult
                {
                    ClaimId = existing.ClaimId,
                    ClaimVersion = existing.ClaimVersion,
                    ClaimType = existing.ClaimType,
                    TransmissionStatus = existing.Status,
                    TransmissionId = existing.TransmissionId,
                    SubmissionId = existing.SubmissionId,
                    ExternalTransactionId = existing.ExternalTransactionId,
                    IdempotencyKey = existing.IdempotencyKey,
                    AcceptedForProcessing = true,
                    ReplayOfExistingTransmission = true
                },
                replayMeta);
        }

        var resolution = await _payerResolver
            .ResolveAsync(request.TenantId, request.PayerId, request.TransactionType(), ct)
            .ConfigureAwait(false);
        if (resolution.Status != PayerResolutionStatus.Found ||
            string.IsNullOrWhiteSpace(resolution.ExternalIdentifierValue))
        {
            return ClaimFail(request, startedAt, stopwatch, 0,
                MapResolution(resolution.Status),
                resolution.Message ?? "Payer could not be resolved for this claim.");
        }

        var record = existing ?? new ClaimTransmissionRecord
        {
            TenantId = request.TenantId,
            ClaimId = request.ClaimId,
            ClaimVersion = request.ClaimVersion,
            GatewayName = GatewayName,
            ClaimType = request.ClaimType,
            TransactionType = request.TransactionType(),
            IdempotencyKey = key,
            CorrelationId = request.CorrelationId,
            PayerId = request.PayerId,
            PatientControlNumber = Truncate(request.ClaimId, 20),
            ServiceLineNumbers = request.ServiceLines.Select(l => l.LineNumber).Where(n => n > 0).ToList(),
            SubmittedAtUtc = startedAt
        };
        record.Status = GatewayClaimTransmissionStatus.Transmitting;
        if (existing is null)
        {
            var (created, stored) = await _transmissions.TryCreateAsync(record, ct).ConfigureAwait(false);
            if (!created)
            {
                if (GatewayClaimTransmissionStatuses.PreventsDuplicateSubmit(stored.Status))
                {
                    RecordClaimMetric(request, stored.Status, GatewayErrorCategory.None, GetElapsed(stopwatch));
                    var replayMeta = ClaimMetadata(request, startedAt, GetElapsed(stopwatch),
                        GatewayTransactionStatus.Completed, GatewayErrorCategory.None, stored.RetryCount,
                        stored.ExternalTransactionId);
                    Log(replayMeta);
                    return GatewayResponse<GatewayClaimSubmissionResult>.Success(
                        new GatewayClaimSubmissionResult
                        {
                            ClaimId = stored.ClaimId,
                            ClaimVersion = stored.ClaimVersion,
                            ClaimType = stored.ClaimType,
                            TransmissionStatus = stored.Status,
                            TransmissionId = stored.TransmissionId,
                            SubmissionId = stored.SubmissionId,
                            ExternalTransactionId = stored.ExternalTransactionId,
                            IdempotencyKey = stored.IdempotencyKey,
                            AcceptedForProcessing = true,
                            ReplayOfExistingTransmission = true
                        },
                        replayMeta);
                }

                record = stored;
            }
        }
        else
        {
            await _transmissions.SaveAsync(record, ct).ConfigureAwait(false);
        }

        var usage = _options.Value.IsProduction ? "P" : "T";
        var stediRequest = StediClaimMapper.ToStediRequest(request, resolution.ExternalIdentifierValue, usage);

        StediClaimApiResult apiResult;
        try
        {
            apiResult = await _claimClient.SubmitAsync(request.ClaimType, stediRequest, key, ct)
                .ConfigureAwait(false);
        }
        catch (StediApiException ex)
        {
            record.Status = GatewayClaimTransmissionStatus.Failed;
            record.CompletedAtUtc = _timeProvider.GetUtcNow();
            record.RetryCount = ex.RetryCount;
            record.ErrorCategory = ex.Category;
            record.ErrorMessage = ex.Message;
            await _transmissions.SaveAsync(record, ct).ConfigureAwait(false);
            return ClaimFail(request, startedAt, stopwatch, ex.RetryCount, ex.Category, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Stedi claim submission for tenant {TenantId} claimType={ClaimType}",
                SanitizeForLog(request.TenantId), request.ClaimType);
            record.Status = GatewayClaimTransmissionStatus.Failed;
            record.ErrorCategory = GatewayErrorCategory.Internal;
            await _transmissions.SaveAsync(record, ct).ConfigureAwait(false);
            return ClaimFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Internal,
                "Unexpected error executing the Stedi claim submission.");
        }

        var result = StediClaimMapper.ToCanonical(
            request, apiResult.Response, record.TransmissionId, key, replay: false);
        record.Status = result.TransmissionStatus;
        record.SubmissionId = result.SubmissionId;
        record.ExternalTransactionId = result.ExternalTransactionId ?? apiResult.ExternalTransactionId;
        record.CompletedAtUtc = _timeProvider.GetUtcNow();
        record.RetryCount = apiResult.RetryCount;
        record.ErrorCategory = result.AcceptedForProcessing
            ? GatewayErrorCategory.None
            : GatewayErrorCategory.PayerRejected;
        if (!result.AcceptedForProcessing)
        {
            record.ErrorMessage = result.Errors.FirstOrDefault();
        }

        await _transmissions.SaveAsync(record, ct).ConfigureAwait(false);

        var txStatus = result.AcceptedForProcessing
            ? GatewayTransactionStatus.Completed
            : GatewayTransactionStatus.Rejected;
        var metadata = ClaimMetadata(
            request, startedAt, GetElapsed(stopwatch), txStatus, record.ErrorCategory,
            apiResult.RetryCount, record.ExternalTransactionId);
        Log(metadata);
        RecordClaimMetric(request, record.Status, record.ErrorCategory, metadata.Latency);
        return GatewayResponse<GatewayClaimSubmissionResult>.Success(result, metadata);
    }

    public async Task<GatewayResponse<ClaimAttachmentSubmissionResult>> SubmitAttachmentAsync(
        ClaimAttachmentSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();

        var configErrors = _options.Value.Validate();
        if (configErrors.Count > 0)
        {
            return AttachmentConfigFail(request, startedAt, stopwatch,
                "Stedi gateway is not configured correctly: " + string.Join(" ", configErrors));
        }

        if (_attachmentClient is null)
        {
            return AttachmentConfigFail(request, startedAt, stopwatch,
                "Stedi claim attachment client is not registered.");
        }

        var transmission = string.IsNullOrWhiteSpace(request.TransmissionId)
            ? null
            : await _transmissions.GetByIdAsync(request.TransmissionId, cancellationToken).ConfigureAwait(false);
        var payerId = request.PayerId ?? transmission?.PayerId;
        var tenantId = !string.IsNullOrWhiteSpace(transmission?.TenantId) ? transmission.TenantId : request.TenantId;
        var resolution = await _payerResolver
            .ResolveAsync(tenantId, payerId, HealthcareTransactionType.ClaimAttachment275, cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Status != PayerResolutionStatus.Found ||
            string.IsNullOrWhiteSpace(resolution.ExternalIdentifierValue))
        {
            var category = MapResolution(resolution.Status);
            var fail = AttachmentConfigFail(
                request, startedAt, stopwatch,
                resolution.Message ?? "Payer could not be resolved for this attachment.",
                category);
            return fail;
        }

        return await _attachments.SubmitAsync(
            GatewayName,
            request,
            async (req, content, ct) =>
            {
                try
                {
                    var apiResult = await _attachmentClient
                        .SubmitAsync(req, content, ct)
                        .ConfigureAwait(false);
                    return new ClaimAttachmentTransportResult(
                        true,
                        apiResult.ExternalTransactionId ?? apiResult.Response.AttachmentId,
                        apiResult.RetryCount,
                        GatewayErrorCategory.None,
                        null);
                }
                catch (StediApiException ex)
                {
                    return new ClaimAttachmentTransportResult(
                        false, null, ex.RetryCount, ex.Category, ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    return new ClaimAttachmentTransportResult(
                        false, null, 0, GatewayErrorCategory.StorageUnavailable, ex.Message);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private GatewayResponse<ClaimAttachmentSubmissionResult> AttachmentConfigFail(
        ClaimAttachmentSubmissionRequest request,
        DateTimeOffset startedAt,
        long stopwatchStart,
        string message,
        GatewayErrorCategory category = GatewayErrorCategory.Configuration)
    {
        var status = category switch
        {
            GatewayErrorCategory.Timeout => GatewayTransactionStatus.TimedOut,
            GatewayErrorCategory.EnrollmentRequired => GatewayTransactionStatus.Failed,
            _ => GatewayTransactionStatus.Failed
        };
        var metadata = new GatewayTransactionMetadata
        {
            GatewayName = GatewayName,
            TransactionType = HealthcareTransactionType.ClaimAttachment275,
            SubmittedAtUtc = startedAt,
            CompletedAtUtc = startedAt + GetElapsed(stopwatchStart),
            Status = status,
            CorrelationId = request.CorrelationId,
            TenantId = request.TenantId,
            Latency = GetElapsed(stopwatchStart),
            ErrorCategory = category
        };
        Log(metadata);
        return GatewayResponse<ClaimAttachmentSubmissionResult>.Failure(message, metadata);
    }

    public async Task<GatewayResponse<GatewayClaimAcknowledgment>> RetrieveAcknowledgmentAsync(
        ClaimAcknowledgmentRetrievalRequest request, CancellationToken cancellationToken = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.GetTimestamp();

        var configErrors = _options.Value.Validate();
        if (configErrors.Count > 0)
        {
            return AckFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Configuration,
                "Stedi gateway is not configured correctly: " + string.Join(" ", configErrors));
        }

        if (_acknowledgmentClient is null)
        {
            return AckFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Configuration,
                "Stedi claim acknowledgment client is not registered.");
        }

        if (string.IsNullOrWhiteSpace(request.ExternalAcknowledgmentId))
        {
            return AckFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Validation,
                "ExternalAcknowledgmentId is required.");
        }

        Stedi277ReportApiResult apiResult;
        try
        {
            apiResult = await _acknowledgmentClient
                .GetReportAsync(request.ExternalAcknowledgmentId.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StediApiException ex)
        {
            return AckFail(request, startedAt, stopwatch, ex.RetryCount, ex.Category, ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error retrieving Stedi 277CA for ack={AckId}",
                SanitizeForLog(request.ExternalAcknowledgmentId));
            return AckFail(request, startedAt, stopwatch, 0, GatewayErrorCategory.Internal,
                "Unexpected error retrieving the Stedi claim acknowledgment.");
        }

        var canonical = StediClaimAcknowledgmentMapper.ToCanonical(
            apiResult.Report, startedAt, request.EventId);
        canonical.CorrelationId = request.CorrelationId;
        canonical.ExternalTransactionId ??= apiResult.ExternalTransactionId;
        if (string.IsNullOrWhiteSpace(canonical.AcknowledgmentId))
        {
            canonical.AcknowledgmentId = request.ExternalAcknowledgmentId.Trim();
        }

        var metadata = AckMetadata(
            request, startedAt, GetElapsed(stopwatch), GatewayTransactionStatus.Completed,
            GatewayErrorCategory.None, apiResult.RetryCount, canonical.ExternalTransactionId);
        Log(metadata);
        return GatewayResponse<GatewayClaimAcknowledgment>.Success(canonical, metadata);
    }

    /// <summary>
    /// Parse a Stedi <c>transaction.processed.v2</c> webhook body into a
    /// vendor-neutral discovery pointer. The body does not contain 277CA content.
    /// </summary>
    public static bool TryParseClaimResponseEvent(string json, out ClaimAcknowledgmentDiscovery discovery) =>
        StediClaimResponseEventParser.TryParse(json, out discovery);

    private GatewayResponse<GatewayClaimAcknowledgment> AckFail(
        ClaimAcknowledgmentRetrievalRequest request,
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
        var metadata = AckMetadata(
            request, startedAt, GetElapsed(stopwatchStart), status, category, retryCount, null);
        Log(metadata);
        return GatewayResponse<GatewayClaimAcknowledgment>.Failure(message, metadata);
    }

    private GatewayTransactionMetadata AckMetadata(
        ClaimAcknowledgmentRetrievalRequest request,
        DateTimeOffset startedAt,
        TimeSpan latency,
        GatewayTransactionStatus status,
        GatewayErrorCategory category,
        int retryCount,
        string? externalTransactionId) =>
        new()
        {
            GatewayName = GatewayName,
            TransactionType = HealthcareTransactionType.ClaimAcknowledgment277CA,
            SubmittedAtUtc = startedAt,
            CompletedAtUtc = startedAt + latency,
            Status = status,
            ExternalTransactionId = externalTransactionId,
            CorrelationId = request.CorrelationId,
            Latency = latency,
            RetryCount = retryCount,
            ErrorCategory = category
        };

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];

    private GatewayResponse<GatewayClaimSubmissionResult> ClaimFail(
        GatewayClaimSubmissionRequest request,
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
        var metadata = ClaimMetadata(
            request, startedAt, GetElapsed(stopwatchStart), status, category, retryCount, null);
        Log(metadata);
        RecordClaimMetric(request, GatewayClaimTransmissionStatus.Failed, category, metadata.Latency);
        return GatewayResponse<GatewayClaimSubmissionResult>.Failure(message, metadata);
    }

    private GatewayTransactionMetadata ClaimMetadata(
        GatewayClaimSubmissionRequest request,
        DateTimeOffset startedAt,
        TimeSpan latency,
        GatewayTransactionStatus status,
        GatewayErrorCategory category,
        int retryCount,
        string? externalTransactionId) =>
        new()
        {
            GatewayName = GatewayName,
            TransactionType = request.TransactionType(),
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

    private static void RecordClaimMetric(
        GatewayClaimSubmissionRequest request,
        GatewayClaimTransmissionStatus status,
        GatewayErrorCategory category,
        TimeSpan latency)
    {
        ChoMetrics.ClaimSubmissions.Add(1,
            new KeyValuePair<string, object?>("cho.gateway", GatewayName),
            new KeyValuePair<string, object?>("cho.claim_type", request.ClaimType.ToString()),
            new KeyValuePair<string, object?>("cho.status", status.ToString()),
            new KeyValuePair<string, object?>("cho.error_category", category.ToString()));
        ChoMetrics.ClaimSubmissionDuration.Record(latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.gateway", GatewayName),
            new KeyValuePair<string, object?>("cho.claim_type", request.ClaimType.ToString()));
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
    // any request/response body. User-influenced values (tenant, correlation,
    // external id) are stripped of newlines to prevent log-entry forging.
    private void Log(GatewayTransactionMetadata metadata) =>
        _logger.LogInformation(
            "Gateway transaction {Gateway} {TransactionType} tenant={TenantId} status={Status} " +
            "category={ErrorCategory} correlation={CorrelationId} latencyMs={LatencyMs} retries={RetryCount} extId={ExternalTransactionId}",
            metadata.GatewayName,
            metadata.TransactionType,
            SanitizeForLog(metadata.TenantId),
            metadata.Status,
            metadata.ErrorCategory,
            SanitizeForLog(metadata.CorrelationId),
            metadata.Latency.TotalMilliseconds,
            metadata.RetryCount,
            SanitizeForLog(metadata.ExternalTransactionId));

    // Remove CR/LF so an attacker cannot forge additional log lines through a
    // tenant id, correlation id, or vendor transaction id.
    private static string? SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static GatewayErrorCategory MapResolution(PayerResolutionStatus status) => status switch
    {
        PayerResolutionStatus.PayerNotFound => GatewayErrorCategory.PayerNotFound,
        PayerResolutionStatus.AmbiguousPayer => GatewayErrorCategory.AmbiguousPayer,
        PayerResolutionStatus.ExternalIdentifierMissing => GatewayErrorCategory.ExternalIdentifierMissing,
        PayerResolutionStatus.TransactionUnsupported => GatewayErrorCategory.NotSupported,
        PayerResolutionStatus.EnrollmentRequired => GatewayErrorCategory.EnrollmentRequired,
        PayerResolutionStatus.PayerDisabled => GatewayErrorCategory.Configuration,
        PayerResolutionStatus.ReferenceDataUnavailable => GatewayErrorCategory.ReferenceDataUnavailable,
        _ => GatewayErrorCategory.Validation
    };

    private TimeSpan GetElapsed(long start) => Stopwatch.GetElapsedTime(start);
}
