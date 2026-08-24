using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Process-local transmission store for Development and tests. Production
/// hosts should register a durable implementation; <c>TryAdd</c> leaves a
/// pre-registered store in place.
/// </summary>
public sealed class InMemoryClaimTransmissionStore : IClaimTransmissionStore
{
    private readonly ConcurrentDictionary<string, ClaimTransmissionRecord> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyToId = new(StringComparer.Ordinal);

    public Task<ClaimTransmissionRecord?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default)
    {
        if (_idempotencyToId.TryGetValue(IdempotencyKey(tenantId, idempotencyKey), out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<ClaimTransmissionRecord?>(null);
    }

    public Task<ClaimTransmissionRecord?> GetByIdAsync(
        string transmissionId, CancellationToken ct = default)
    {
        _byId.TryGetValue(transmissionId, out var record);
        return Task.FromResult(Clone(record));
    }

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindBySubmissionIdAsync(
        string gatewayName, string submissionId, CancellationToken ct = default) =>
        Task.FromResult(Find(r =>
            NamesEqual(r.GatewayName, gatewayName) &&
            ValuesEqual(r.SubmissionId, submissionId)));

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByExternalTransactionIdAsync(
        string gatewayName, string externalTransactionId, CancellationToken ct = default) =>
        Task.FromResult(Find(r =>
            NamesEqual(r.GatewayName, gatewayName) &&
            ValuesEqual(r.ExternalTransactionId, externalTransactionId)));

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByCorrelationIdAsync(
        string gatewayName, string correlationId, CancellationToken ct = default) =>
        Task.FromResult(Find(r =>
            NamesEqual(r.GatewayName, gatewayName) &&
            !string.IsNullOrWhiteSpace(r.CorrelationId) &&
            string.Equals(r.CorrelationId, correlationId, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<ClaimTransmissionRecord>> FindByPatientControlNumberAsync(
        string gatewayName, string patientControlNumber, CancellationToken ct = default)
    {
        IReadOnlyList<ClaimTransmissionRecord> matches = Find(r =>
            NamesEqual(r.GatewayName, gatewayName) &&
            PatientControlMatches(r, patientControlNumber));
        return Task.FromResult(matches);
    }

    public Task SaveAsync(ClaimTransmissionRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        _byId[clone.TransmissionId] = clone;
        _idempotencyToId[IdempotencyKey(clone.TenantId, clone.IdempotencyKey)] = clone.TransmissionId;
        return Task.CompletedTask;
    }

    public Task<(bool Created, ClaimTransmissionRecord Record)> TryCreateAsync(
        ClaimTransmissionRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        var key = IdempotencyKey(clone.TenantId, clone.IdempotencyKey);
        if (!_idempotencyToId.TryAdd(key, clone.TransmissionId))
        {
            var existingId = _idempotencyToId[key];
            _byId.TryGetValue(existingId, out var existing);
            return Task.FromResult((false, Clone(existing) ?? clone));
        }

        _byId[clone.TransmissionId] = clone;
        return Task.FromResult((true, Clone(clone)!));
    }

    private IReadOnlyList<ClaimTransmissionRecord> Find(Func<ClaimTransmissionRecord, bool> predicate) =>
        _byId.Values.Where(predicate).Select(r => Clone(r)!).ToList();

    private static string IdempotencyKey(string tenantId, string idempotencyKey) =>
        $"{tenantId}\u001f{idempotencyKey}";

    private static bool NamesEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool ValuesEqual(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool PatientControlMatches(ClaimTransmissionRecord record, string inbound)
    {
        var stored = record.PatientControlNumber ?? record.ClaimId;
        if (string.IsNullOrWhiteSpace(stored) || string.IsNullOrWhiteSpace(inbound))
        {
            return false;
        }

        if (string.Equals(stored, inbound, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Stedi documents that some payers truncate PCNs to 30 characters.
        if (stored.Length > 30 &&
            string.Equals(stored[..30], inbound, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return inbound.Length > 30 &&
               string.Equals(stored, inbound[..30], StringComparison.OrdinalIgnoreCase);
    }

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
            PayerId = source.PayerId,
            PatientControlNumber = source.PatientControlNumber,
            ServiceLineNumbers = source.ServiceLineNumbers.ToList(),
            SubmittedAtUtc = source.SubmittedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            AcknowledgedAtUtc = source.AcknowledgedAtUtc,
            RetryCount = source.RetryCount,
            ErrorCategory = source.ErrorCategory,
            ErrorMessage = source.ErrorMessage
        };
    }
}
