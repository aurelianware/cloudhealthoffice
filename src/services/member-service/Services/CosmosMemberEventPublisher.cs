using MemberService.Models;
using MemberService.Repositories;
using Microsoft.Extensions.Logging;

namespace MemberService.Services;

/// <summary>
/// Default <see cref="IMemberEventPublisher"/>. Writes events to the append-only
/// Cosmos/Mongo stream via <see cref="IMemberEventRepository"/>.
///
/// Concurrency: repositories surface a version conflict as
/// <see cref="AppendResult.Appended"/> <c>false</c> with no existing event.
/// The publisher retries up to <see cref="MaxVersionRetries"/> with exponential
/// backoff capped at 250 ms before surfacing a <see cref="ConcurrencyException"/>.
///
/// Idempotency: if a caller retries with the same <see cref="MemberEvent.EventId"/>,
/// the repository returns <c>Appended=false</c> with the existing event and the
/// publisher short-circuits.
/// </summary>
public sealed class CosmosMemberEventPublisher : IMemberEventPublisher
{
    private const int MaxVersionRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    private readonly IMemberEventRepository _repository;
    private readonly ILogger<CosmosMemberEventPublisher> _logger;

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

        // Short-circuit: if a prior write with this EventId exists, return it.
        var existing = await _repository.GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
        if (existing != null)
        {
            _logger.LogDebug(
                "MemberEvent {EventId} already present for {Tenant}:{Member} (idempotent no-op)",
                SanitizeForLog(evt.EventId), SanitizeForLog(evt.TenantId), SanitizeForLog(evt.MemberId));
            return existing;
        }

        for (int attempt = 0; attempt < MaxVersionRetries; attempt++)
        {
            evt.Version = await _repository.GetNextVersionAsync(evt.TenantId, evt.MemberId, ct);
            var result = await _repository.AppendAsync(evt, ct);
            if (result.Appended) return result.Event;

            // Appended=false has two causes:
            //   1. Same EventId already exists → the repository returns it as result.Event.
            //      Short-circuit with the existing row.
            //   2. Version slot taken by a concurrent writer (unique-key violation) →
            //      result.Event is the envelope we just tried. Re-fetch by EventId to
            //      disambiguate, then either return the existing or retry with a new version.
            var refetch = await _repository.GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            if (refetch != null) return refetch;

            _logger.LogWarning(
                "MemberEvent version {Version} conflict for {Tenant}:{Member}; retry {Attempt}/{Max}",
                evt.Version, SanitizeForLog(evt.TenantId), SanitizeForLog(evt.MemberId), attempt + 1, MaxVersionRetries);

            if (attempt + 1 < MaxVersionRetries)
            {
                await Task.Delay(BackoffMs[attempt], ct);
            }
        }

        throw new ConcurrencyException(evt.TenantId, evt.MemberId, MaxVersionRetries);
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
