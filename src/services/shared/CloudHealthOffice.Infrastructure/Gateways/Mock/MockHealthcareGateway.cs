using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways.Capabilities;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways.Mock;

/// <summary>
/// Development/test healthcare transaction gateway. Advertises the
/// <see cref="GatewayCapability.Eligibility"/> capability and returns
/// deterministic, tenant-scoped eligibility responses from an in-memory
/// roster — it does <b>not</b> contact any external payer or clearinghouse.
///
/// Its purpose is to prove the gateway abstraction end to end and to give
/// automated tests a real implementation to resolve. It supports eligibility
/// and claim submission. 277CA retrieve is not advertised; development
/// injection uses the shared canonical processor.
///
/// Logging discipline: only non-PHI <see cref="GatewayTransactionMetadata"/>
/// is logged. Subscriber identifiers, names, and dates of birth are never
/// written to logs.
/// </summary>
public sealed class MockHealthcareGateway : IEligibilityGateway, IClaimSubmissionGateway
{
    /// <summary>The name this gateway registers under and is resolved by.</summary>
    public const string GatewayName = "Mock";

    private static readonly IReadOnlySet<GatewayCapability> SupportedCapabilities =
        new HashSet<GatewayCapability> { GatewayCapability.Eligibility, GatewayCapability.ClaimSubmission };

    private readonly ILogger<MockHealthcareGateway> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IClaimTransmissionStore _transmissions;

    // Tenant-scoped roster: tenantId -> (subscriberId -> seeded member).
    // A member is only visible within the tenant it was seeded under, which is
    // what enforces tenant isolation for the mock.
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, MockMember>> _roster;

    public MockHealthcareGateway(
        ILogger<MockHealthcareGateway> logger,
        TimeProvider? timeProvider = null,
        IClaimTransmissionStore? transmissions = null)
        : this(logger, DefaultRoster(), timeProvider, transmissions)
    {
    }

    internal MockHealthcareGateway(
        ILogger<MockHealthcareGateway> logger,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, MockMember>> roster,
        TimeProvider? timeProvider = null,
        IClaimTransmissionStore? transmissions = null)
    {
        _logger = logger;
        _roster = roster;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transmissions = transmissions ?? new InMemoryClaimTransmissionStore();
    }

    public string Name => GatewayName;

    public IReadOnlySet<GatewayCapability> Capabilities => SupportedCapabilities;

    public Task<GatewayResponse<GatewayEligibilityResponse>> CheckEligibilityAsync(
        GatewayEligibilityRequest request, CancellationToken ct = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var timestamp = Stopwatch.GetTimestamp();

        // Validation — reject missing tenant/subscriber explicitly. Tenant is
        // mandatory so a caller can never accidentally query across tenants.
        if (string.IsNullOrWhiteSpace(request.TenantId) ||
            string.IsNullOrWhiteSpace(request.ResolveSubscriberMemberId()))
        {
            var invalid = BuildMetadata(
                request, startedAt, GatewayTransactionStatus.Failed,
                GatewayErrorCategory.Validation, Stopwatch.GetElapsedTime(timestamp));
            Log(invalid);
            return Task.FromResult(GatewayResponse<GatewayEligibilityResponse>.Failure(
                "TenantId and SubscriberId are required.", invalid));
        }

        var response = Evaluate(request);

        // Both an active and an "inactive/not-on-file" result are completed
        // transactions — the inquiry ran and produced a normalized answer.
        var metadata = BuildMetadata(
            request, startedAt, GatewayTransactionStatus.Completed, GatewayErrorCategory.None,
            Stopwatch.GetElapsedTime(timestamp));

        Log(metadata);

        return Task.FromResult(GatewayResponse<GatewayEligibilityResponse>.Success(response, metadata));
    }

    public async Task<GatewayResponse<GatewayClaimSubmissionResult>> SubmitClaimAsync(
        GatewayClaimSubmissionRequest request, CancellationToken ct = default)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var timestamp = Stopwatch.GetTimestamp();
        var key = request.ResolveIdempotencyKey();

        var validation = GatewayClaimSubmissionValidator.Validate(request);
        if (validation is not null)
        {
            return ClaimFail(request, startedAt, timestamp, GatewayErrorCategory.Validation, validation);
        }

        var existing = await _transmissions.GetByIdempotencyKeyAsync(request.TenantId, key, ct)
            .ConfigureAwait(false);
        if (existing is not null &&
            existing.Status is GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
                or GatewayClaimTransmissionStatus.Transmitted)
        {
            var replay = ToResult(existing, replay: true);
            var meta = ClaimMetadata(request, startedAt, GatewayTransactionStatus.Completed,
                GatewayErrorCategory.None, Stopwatch.GetElapsedTime(timestamp), existing);
            Log(meta);
            return GatewayResponse<GatewayClaimSubmissionResult>.Success(replay, meta);
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
            PatientControlNumber = string.IsNullOrEmpty(request.ClaimId)
                ? request.ClaimId
                : request.ClaimId.Length <= 20 ? request.ClaimId : request.ClaimId[..20],
            SubmittedAtUtc = startedAt
        };
        record.Status = GatewayClaimTransmissionStatus.Transmitting;
        await _transmissions.SaveAsync(record, ct).ConfigureAwait(false);

        var completed = _timeProvider.GetUtcNow();
        record.Status = GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway;
        record.CompletedAtUtc = completed;
        record.SubmissionId = $"mock-{record.TransmissionId}";
        record.ExternalTransactionId = record.SubmissionId;
        record.ErrorCategory = GatewayErrorCategory.None;
        await _transmissions.SaveAsync(record, ct).ConfigureAwait(false);

        var metadata = ClaimMetadata(request, startedAt, GatewayTransactionStatus.Completed,
            GatewayErrorCategory.None, Stopwatch.GetElapsedTime(timestamp), record);
        Log(metadata);
        return GatewayResponse<GatewayClaimSubmissionResult>.Success(ToResult(record, replay: false), metadata);
    }

    private GatewayResponse<GatewayClaimSubmissionResult> ClaimFail(
        GatewayClaimSubmissionRequest request,
        DateTimeOffset startedAt,
        long timestamp,
        GatewayErrorCategory category,
        string message)
    {
        var metadata = ClaimMetadata(
            request, startedAt, GatewayTransactionStatus.Failed, category,
            Stopwatch.GetElapsedTime(timestamp), record: null);
        Log(metadata);
        return GatewayResponse<GatewayClaimSubmissionResult>.Failure(message, metadata);
    }

    private static GatewayClaimSubmissionResult ToResult(ClaimTransmissionRecord record, bool replay) =>
        new()
        {
            ClaimId = record.ClaimId,
            ClaimVersion = record.ClaimVersion,
            ClaimType = record.ClaimType,
            TransmissionStatus = record.Status,
            TransmissionId = record.TransmissionId,
            SubmissionId = record.SubmissionId,
            ExternalTransactionId = record.ExternalTransactionId,
            IdempotencyKey = record.IdempotencyKey,
            AcceptedForProcessing = record.Status is
                GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway or
                GatewayClaimTransmissionStatus.Transmitted,
            ReplayOfExistingTransmission = replay
        };

    private GatewayTransactionMetadata ClaimMetadata(
        GatewayClaimSubmissionRequest request,
        DateTimeOffset startedAt,
        GatewayTransactionStatus status,
        GatewayErrorCategory errorCategory,
        TimeSpan latency,
        ClaimTransmissionRecord? record) =>
        new()
        {
            GatewayName = GatewayName,
            TransactionType = request.TransactionType(),
            SubmittedAtUtc = startedAt,
            CompletedAtUtc = startedAt + latency,
            Status = status,
            ExternalTransactionId = record?.ExternalTransactionId,
            CorrelationId = request.CorrelationId,
            TenantId = request.TenantId,
            Latency = latency,
            RetryCount = record?.RetryCount ?? 0,
            ErrorCategory = errorCategory
        };

    private GatewayEligibilityResponse Evaluate(GatewayEligibilityRequest request)
    {
        // Only members seeded under the request's tenant are visible.
        if (_roster.TryGetValue(request.TenantId, out var members) &&
            members.TryGetValue(request.ResolveSubscriberMemberId(), out var member))
        {
            return new GatewayEligibilityResponse
            {
                IsEligible = true,
                CoverageStatus = GatewayCoverageStatus.Active,
                StatusCode = "1",
                PlanId = member.PlanId,
                PlanName = member.PlanName,
                GroupNumber = member.GroupNumber,
                CoverageStart = member.CoverageStart,
                CoverageEnd = member.CoverageEnd,
                Benefits = member.Benefits
                    .Select(b => new GatewayEligibilityBenefit
                    {
                        ServiceTypeCode = b.ServiceTypeCode,
                        ServiceTypeName = b.ServiceTypeName,
                        CoverageLevel = b.CoverageLevel,
                        InNetwork = b.InNetwork,
                        CopayAmount = b.CopayAmount,
                        CoinsurancePercent = b.CoinsurancePercent
                    })
                    .ToList()
            };
        }

        // Not on the tenant's roster → deterministic inactive result. The
        // response never carries another tenant's plan data.
        return new GatewayEligibilityResponse
        {
            IsEligible = false,
            CoverageStatus = GatewayCoverageStatus.Inactive,
            StatusCode = "6",
            RejectionReason = "No active coverage on file for this member under the requesting tenant."
        };
    }

    private GatewayTransactionMetadata BuildMetadata(
        GatewayEligibilityRequest request,
        DateTimeOffset startedAt,
        GatewayTransactionStatus status,
        GatewayErrorCategory errorCategory,
        TimeSpan latency) =>
        new()
        {
            GatewayName = GatewayName,
            TransactionType = HealthcareTransactionType.Eligibility270271,
            SubmittedAtUtc = startedAt,
            CompletedAtUtc = startedAt + latency,
            Status = status,
            ExternalTransactionId = $"mock-{Guid.NewGuid():N}",
            CorrelationId = request.CorrelationId,
            TenantId = request.TenantId,
            Latency = latency,
            RetryCount = 0,
            ErrorCategory = errorCategory
        };

    // Logs ONLY non-PHI metadata. No subscriber id, name, or DOB is ever
    // written here.
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

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, MockMember>> DefaultRoster()
    {
        // Deterministic development seed. Two tenants, each with an active
        // member. The same subscriber id under a different tenant is not
        // visible, which the tenant-boundary test relies on.
        var alpha = new Dictionary<string, MockMember>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUB-1001"] = new MockMember
            {
                PlanId = "ALPHA-PPO-GOLD",
                PlanName = "Alpha PPO Gold",
                GroupNumber = "GRP-ALPHA-01",
                CoverageStart = new DateOnly(2026, 1, 1),
                CoverageEnd = new DateOnly(2026, 12, 31),
                Benefits = new List<MockBenefit>
                {
                    new()
                    {
                        ServiceTypeCode = "30",
                        ServiceTypeName = "Health Benefit Plan Coverage",
                        CoverageLevel = "FAM",
                        InNetwork = true,
                        CopayAmount = 25m,
                        CoinsurancePercent = 0.20m
                    }
                }
            }
        };

        var beta = new Dictionary<string, MockMember>(StringComparer.OrdinalIgnoreCase)
        {
            ["SUB-2002"] = new MockMember
            {
                PlanId = "BETA-HMO-BASE",
                PlanName = "Beta HMO Base",
                GroupNumber = "GRP-BETA-07",
                CoverageStart = new DateOnly(2026, 1, 1),
                CoverageEnd = new DateOnly(2026, 12, 31),
                Benefits = new List<MockBenefit>
                {
                    new()
                    {
                        ServiceTypeCode = "30",
                        ServiceTypeName = "Health Benefit Plan Coverage",
                        CoverageLevel = "IND",
                        InNetwork = true,
                        CopayAmount = 40m,
                        CoinsurancePercent = 0m
                    }
                }
            }
        };

        return new Dictionary<string, IReadOnlyDictionary<string, MockMember>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant-alpha"] = alpha,
            ["tenant-beta"] = beta
        };
    }

    /// <summary>Seeded member record used by the mock roster. Test-visible.</summary>
    internal sealed class MockMember
    {
        public string PlanId { get; init; } = string.Empty;
        public string PlanName { get; init; } = string.Empty;
        public string GroupNumber { get; init; } = string.Empty;
        public DateOnly? CoverageStart { get; init; }
        public DateOnly? CoverageEnd { get; init; }
        public List<MockBenefit> Benefits { get; init; } = new();
    }

    /// <summary>Seeded benefit line used by the mock roster. Test-visible.</summary>
    internal sealed class MockBenefit
    {
        public string ServiceTypeCode { get; init; } = string.Empty;
        public string ServiceTypeName { get; init; } = string.Empty;
        public string? CoverageLevel { get; init; }
        public bool InNetwork { get; init; } = true;
        public decimal? CopayAmount { get; init; }
        public decimal? CoinsurancePercent { get; init; }
    }
}
