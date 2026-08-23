using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Process-local transmission store for Development and tests. Production
/// hosts should register a durable implementation; <c>TryAdd</c> leaves a
/// pre-registered store in place.
/// </summary>
public sealed class InMemoryClaimTransmissionStore : IClaimTransmissionStore
{
    private readonly ConcurrentDictionary<string, ClaimTransmissionRecord> _items = new(StringComparer.Ordinal);

    public Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default)
    {
        _items.TryGetValue(Key(tenantId, idempotencyKey), out var record);
        return Task.FromResult(Clone(record));
    }

    public Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default)
    {
        _items[Key(record.TenantId, record.IdempotencyKey)] = Clone(record)!;
        return Task.CompletedTask;
    }

    private static string Key(string tenantId, string idempotencyKey) =>
        $"{tenantId}\u001f{idempotencyKey}";

    private static ClaimTransmissionRecord? Clone(ClaimTransmissionRecord? source)
    {
        if (source is null)
        {
            return null;
        }

        return new ClaimTransmissionRecord
        {
            TransmissionId = source.TransmissionId,
            TenantId = source.TenantId,
            ClaimId = source.ClaimId,
            ClaimVersion = source.ClaimVersion,
            GatewayName = source.GatewayName,
            ClaimType = source.ClaimType,
            TransactionType = source.TransactionType,
            Status = source.Status,
            IdempotencyKey = source.IdempotencyKey,
            SubmissionId = source.SubmissionId,
            ExternalTransactionId = source.ExternalTransactionId,
            CorrelationId = source.CorrelationId,
            SubmittedAtUtc = source.SubmittedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            RetryCount = source.RetryCount,
            ErrorCategory = source.ErrorCategory,
            ErrorMessage = source.ErrorMessage
        };
    }
}
