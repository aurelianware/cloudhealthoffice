using System.Collections.Concurrent;

namespace CloudHealthOffice.Infrastructure.Gateways;

public sealed class InMemoryClaimAttachmentTransmissionStore : IClaimAttachmentTransmissionStore
{
    private readonly ConcurrentDictionary<string, ClaimAttachmentTransmissionRecord> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotencyToId = new(StringComparer.Ordinal);

    public Task<ClaimAttachmentTransmissionRecord?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default)
    {
        if (_idempotencyToId.TryGetValue(IdempotencyKey(tenantId, idempotencyKey), out var id))
        {
            _byId.TryGetValue(id, out var record);
            return Task.FromResult(Clone(record));
        }

        return Task.FromResult<ClaimAttachmentTransmissionRecord?>(null);
    }

    public Task<ClaimAttachmentTransmissionRecord?> GetByIdAsync(
        string attachmentTransmissionId, CancellationToken ct = default)
    {
        _byId.TryGetValue(attachmentTransmissionId, out var record);
        return Task.FromResult(Clone(record));
    }

    public Task<IReadOnlyList<ClaimAttachmentTransmissionRecord>> ListByClaimTransmissionIdAsync(
        string claimTransmissionId, CancellationToken ct = default)
    {
        IReadOnlyList<ClaimAttachmentTransmissionRecord> matches = _byId.Values
            .Where(r => string.Equals(r.ClaimTransmissionId, claimTransmissionId, StringComparison.Ordinal))
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<ClaimAttachmentTransmissionRecord>> FindByChecksumAsync(
        string tenantId, string checksumSha256, CancellationToken ct = default)
    {
        IReadOnlyList<ClaimAttachmentTransmissionRecord> matches = _byId.Values
            .Where(r =>
                string.Equals(r.TenantId, tenantId, StringComparison.Ordinal) &&
                string.Equals(r.ChecksumSha256, checksumSha256, StringComparison.OrdinalIgnoreCase))
            .Select(r => Clone(r)!)
            .ToList();
        return Task.FromResult(matches);
    }

    public Task SaveAsync(ClaimAttachmentTransmissionRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        _byId[clone.AttachmentTransmissionId] = clone;
        _idempotencyToId[IdempotencyKey(clone.TenantId, clone.IdempotencyKey)] = clone.AttachmentTransmissionId;
        return Task.CompletedTask;
    }

    public Task<(bool Created, ClaimAttachmentTransmissionRecord Record)> TryCreateAsync(
        ClaimAttachmentTransmissionRecord record, CancellationToken ct = default)
    {
        var clone = Clone(record)!;
        var key = IdempotencyKey(clone.TenantId, clone.IdempotencyKey);
        if (!_idempotencyToId.TryAdd(key, clone.AttachmentTransmissionId))
        {
            var existingId = _idempotencyToId[key];
            _byId.TryGetValue(existingId, out var existing);
            return Task.FromResult((false, Clone(existing) ?? clone));
        }

        _byId[clone.AttachmentTransmissionId] = clone;
        return Task.FromResult((true, Clone(clone)!));
    }

    private static string IdempotencyKey(string tenantId, string idempotencyKey) =>
        $"{tenantId}\u001f{idempotencyKey}";

    internal static ClaimAttachmentTransmissionRecord? Clone(ClaimAttachmentTransmissionRecord? source)
    {
        if (source is null)
        {
            return null;
        }

        return new ClaimAttachmentTransmissionRecord
        {
            AttachmentTransmissionId = source.AttachmentTransmissionId,
            TenantId = source.TenantId,
            ClaimId = source.ClaimId,
            ClaimTransmissionId = source.ClaimTransmissionId,
            AttachmentId = source.AttachmentId,
            AttachmentVersion = source.AttachmentVersion,
            GatewayName = source.GatewayName,
            PayerId = source.PayerId,
            ClaimType = source.ClaimType,
            AttachmentType = source.AttachmentType,
            Mode = source.Mode,
            AssociationLevel = source.AssociationLevel,
            ServiceLineNumber = source.ServiceLineNumber,
            AttachmentControlNumber = source.AttachmentControlNumber,
            ContentType = source.ContentType,
            ContentLength = source.ContentLength,
            ChecksumSha256 = source.ChecksumSha256,
            ContentContainer = source.ContentContainer,
            ContentStorageKey = source.ContentStorageKey,
            ExternalTransactionId = source.ExternalTransactionId,
            Status = source.Status,
            IdempotencyKey = source.IdempotencyKey,
            CorrelationId = source.CorrelationId,
            SubmittedAtUtc = source.SubmittedAtUtc,
            CompletedAtUtc = source.CompletedAtUtc,
            RetryCount = source.RetryCount,
            ErrorCategory = source.ErrorCategory,
            ErrorMessage = source.ErrorMessage
        };
    }
}
