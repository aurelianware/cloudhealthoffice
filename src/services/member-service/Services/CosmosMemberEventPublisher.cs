using MemberService.Models;
using MemberService.Repositories;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// Default <see cref="IMemberEventPublisher"/>. Writes events to the append-only
/// Cosmos/Mongo stream via <see cref="IMemberEventRepository"/>. Concurrent writers
/// are retried on version conflict.
/// </summary>
public sealed class CosmosMemberEventPublisher : IMemberEventPublisher
{
    private readonly IMemberEventRepository _repository;
    private readonly ILogger<CosmosMemberEventPublisher> _logger;
    private const int MaxVersionRetries = 5;

    public CosmosMemberEventPublisher(
        IMemberEventRepository repository,
        ILogger<CosmosMemberEventPublisher> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<MemberEvent> PublishAsync(MemberEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("MemberEvent.EventId is required (client-supplied idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.MemberId))
            throw new ArgumentException("MemberEvent must have TenantId and MemberId");

        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = MemberEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
        if (string.IsNullOrEmpty(evt.Id)) evt.Id = evt.EventId;
        if (evt.SchemaVersion <= 0) evt.SchemaVersion = 1;

        // If a prior write with this EventId already exists, short-circuit and return it.
        var existing = await _repository.GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug("MemberEvent {EventId} already present for {Tenant}:{Member} (idempotent no-op)",
                evt.EventId, evt.TenantId, evt.MemberId);
            return existing;
        }

        for (int attempt = 1; attempt <= MaxVersionRetries; attempt++)
        {
            evt.Version = await _repository.GetNextVersionAsync(evt.TenantId, evt.MemberId, ct);
            var result = await _repository.AppendAsync(evt, ct);
            if (result.Appended) return result.Event;

            // Duplicate-key: either same EventId raced us, or a concurrent writer took our version slot.
            var refetch = await _repository.GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            if (refetch != null) return refetch;

            _logger.LogWarning(
                "MemberEvent version conflict for {Tenant}:{Member} v{Version}; retry {Attempt}/{Max}",
                evt.TenantId, evt.MemberId, evt.Version, attempt, MaxVersionRetries);
        }

        throw new InvalidOperationException(
            $"Failed to append MemberEvent after {MaxVersionRetries} version-retry attempts " +
            $"for {evt.TenantId}:{evt.MemberId}");
    }
}
