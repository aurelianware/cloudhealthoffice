using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Process-local 276/277 inquiry snapshots for Development and tests.
/// </summary>
public sealed class InMemoryClaimStatusInquiryStore : IClaimStatusInquiryStore
{
    private readonly ConcurrentDictionary<string, ClaimStatusInquiryRecord> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyToId = new(StringComparer.Ordinal);

    public Task<ClaimStatusInquiryRecord?> GetByIdAsync(
        string inquiryId, CancellationToken ct = default)
    {
        _byId.TryGetValue(inquiryId, out var record);
        return Task.FromResult(Clone(record));
    }

    public Task<ClaimStatusInquiryRecord?> GetByExternalTransactionIdAsync(
        string tenantId, string gatewayName, string externalTransactionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(externalTransactionId))
        {
            return Task.FromResult<ClaimStatusInquiryRecord?>(null);
        }

        var match = _byId.Values.FirstOrDefault(r =>
            string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
            string.Equals(r.GatewayName, gatewayName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.ExternalTransactionId, externalTransactionId, StringComparison.Ordinal));
        return Task.FromResult(Clone(match));
    }

    public Task<IReadOnlyList<ClaimStatusInquiryRecord>> ListByTransmissionIdAsync(
        string transmissionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClaimStatusInquiryRecord>>(
            _byId.Values
                .Where(r => string.Equals(r.TransmissionId, transmissionId, StringComparison.Ordinal))
                .OrderBy(r => r.RequestedAtUtc)
                .Select(r => Clone(r)!)
                .ToList());

    public Task<IReadOnlyList<ClaimStatusInquiryRecord>> ListByTenantAndClaimIdAsync(
        string tenantId, string claimId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClaimStatusInquiryRecord>>(
            _byId.Values
                .Where(r =>
                    string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
                    string.Equals(r.ClaimId, claimId, StringComparison.Ordinal))
                .OrderBy(r => r.RequestedAtUtc)
                .Select(r => Clone(r)!)
                .ToList());

    public Task SaveAsync(ClaimStatusInquiryRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        _byId[clone.InquiryId] = clone;
        _idempotencyToId[clone.IdempotencyKey] = clone.InquiryId;
        return Task.CompletedTask;
    }

    public Task<(bool Created, ClaimStatusInquiryRecord Record)> TryCreateAsync(
        ClaimStatusInquiryRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        if (!_byId.TryAdd(clone.InquiryId, clone))
        {
            _byId.TryGetValue(clone.InquiryId, out var existing);
            return Task.FromResult((false, Clone(existing) ?? clone));
        }

        if (!_idempotencyToId.TryAdd(clone.IdempotencyKey, clone.InquiryId))
        {
            _byId.TryRemove(clone.InquiryId, out _);
            var winnerId = _idempotencyToId[clone.IdempotencyKey];
            _byId.TryGetValue(winnerId, out var winner);
            return Task.FromResult((false, Clone(winner) ?? clone));
        }

        return Task.FromResult((true, Clone(clone)!));
    }

    private static ClaimStatusInquiryRecord? Clone(ClaimStatusInquiryRecord? source)
    {
        if (source is null)
        {
            return null;
        }

        return new ClaimStatusInquiryRecord
        {
            InquiryId = source.InquiryId,
            TenantId = source.TenantId,
            ClaimId = source.ClaimId,
            TransmissionId = source.TransmissionId,
            GatewayName = source.GatewayName,
            PayerId = source.PayerId,
            RequestedAtUtc = source.RequestedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            NormalizedStatus = source.NormalizedStatus,
            StatusCategoryCode = source.StatusCategoryCode,
            StatusCode = source.StatusCode,
            StatusDate = source.StatusDate,
            PayerClaimControlNumber = source.PayerClaimControlNumber,
            PatientControlNumber = source.PatientControlNumber,
            ExternalTransactionId = source.ExternalTransactionId,
            CorrelationId = source.CorrelationId,
            RetryCount = source.RetryCount,
            ErrorCategory = source.ErrorCategory,
            ErrorMessage = source.ErrorMessage,
            ServiceLineNumber = source.ServiceLineNumber,
            Response = source.Response
        };
    }
}
