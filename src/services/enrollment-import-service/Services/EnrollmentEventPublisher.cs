using System.Diagnostics.Metrics;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;

namespace EnrollmentImportService.Services;

public interface IEnrollmentEventPublisher
{
    Task<EnrollmentEvent> PublishAsync(EnrollmentEvent evt, CancellationToken ct = default);
}

public sealed class EnrollmentConcurrencyException : Exception
{
    public EnrollmentConcurrencyException(string tenantId, string memberId, int attempts)
        : base($"Failed to append enrollment event for {tenantId}:{memberId} after {attempts} version-conflict retries.")
    {
        TenantId = tenantId;
        MemberId = memberId;
        Attempts = attempts;
    }

    public string TenantId { get; }
    public string MemberId { get; }
    public int Attempts { get; }
}

/// <summary>
/// Default <see cref="IEnrollmentEventPublisher"/>. Mirrors
/// <c>CosmosMemberEventPublisher</c>: idempotent on EventId, retries up to
/// <see cref="MaxVersionRetries"/> on version conflict with bounded backoff.
///
/// Replay observability: every dedup short-circuit (caller re-submitted an EventId we
/// already stored) increments <c>enrollment_event_replay_total</c> and emits a structured
/// log line including the original event's <c>OccurredAt</c>. Silent dedup is correct
/// behavior but invisible operations hide real problems — when this counter spikes,
/// someone re-played an 834 (intentional reprocessing or pipeline duplicate delivery).
/// </summary>
public sealed class EnrollmentEventPublisher : IEnrollmentEventPublisher
{
    private const int MaxVersionRetries = 5;
    private static readonly int[] BackoffMs = { 2, 5, 25, 100, 250 };

    public const string MeterName = "EnrollmentImportService";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Counter incremented every time the publisher dedupes an event because an entry
    /// with the same EventId already exists. Tagged with <c>tenantId</c>,
    /// <c>eventType</c>, <c>source</c> (834|manual).
    /// </summary>
    public static readonly Counter<long> ReplayCounter =
        Meter.CreateCounter<long>(
            "enrollment_event_replay_total",
            unit: "{events}",
            description: "Number of enrollment events deduped on replay (existing EventId).");

    private readonly IEnrollmentEventRepository _repository;
    private readonly ILogger<EnrollmentEventPublisher> _logger;

    public EnrollmentEventPublisher(
        IEnrollmentEventRepository repository,
        ILogger<EnrollmentEventPublisher> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<EnrollmentEvent> PublishAsync(EnrollmentEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (string.IsNullOrEmpty(evt.EventId))
            throw new ArgumentException("EnrollmentEvent.EventId is required (idempotency key)");
        if (string.IsNullOrEmpty(evt.TenantId) || string.IsNullOrEmpty(evt.MemberId))
            throw new ArgumentException("EnrollmentEvent must have TenantId and MemberId");

        if (evt.OccurredAt == default) evt.OccurredAt = DateTime.UtcNow;
        if (string.IsNullOrEmpty(evt.PartitionKey))
            evt.PartitionKey = EnrollmentEvent.BuildPartitionKey(evt.TenantId, evt.MemberId);
        if (string.IsNullOrEmpty(evt.Id)) evt.Id = evt.EventId;
        if (evt.SchemaVersion <= 0) evt.SchemaVersion = 1;

        var existing = await _repository.GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
        if (existing != null)
        {
            RecordReplay(existing, "preflight");
            return existing;
        }

        for (int attempt = 0; attempt < MaxVersionRetries; attempt++)
        {
            evt.Version = await _repository.GetNextVersionAsync(evt.TenantId, evt.MemberId, ct);
            var result = await _repository.AppendAsync(evt, ct);
            if (result.Appended) return result.Event;

            // Either a same-EventId race (refetch returns it) or a version-slot collision
            // (refetch returns null; recompute version and retry).
            var refetch = await _repository.GetByIdAsync(evt.TenantId, evt.MemberId, evt.EventId, ct);
            if (refetch != null)
            {
                RecordReplay(refetch, "race");
                return refetch;
            }

            _logger.LogWarning(
                "EnrollmentEvent version {Version} conflict for {Tenant}:{Member}; retry {Attempt}/{Max}",
                evt.Version, Sanitize(evt.TenantId), Sanitize(evt.MemberId), attempt + 1, MaxVersionRetries);

            if (attempt + 1 < MaxVersionRetries)
                await Task.Delay(BackoffMs[attempt], ct);
        }

        throw new EnrollmentConcurrencyException(evt.TenantId, evt.MemberId, MaxVersionRetries);
    }

    private void RecordReplay(EnrollmentEvent existing, string detection)
    {
        ReplayCounter.Add(1,
            new KeyValuePair<string, object?>("tenantId", Sanitize(existing.TenantId)),
            new KeyValuePair<string, object?>("eventType", existing.EventType.ToString()),
            new KeyValuePair<string, object?>("source", existing.Source ?? "unknown"));

        // Debug-level so it doesn't flood production logs but is grep-able when triaging.
        _logger.LogDebug(
            "EnrollmentEvent replay deduped: eventId={EventId} tenant={Tenant} member={Member} type={Type} originallyOccurredAt={OccurredAt:o} detection={Detection}",
            Sanitize(existing.EventId),
            Sanitize(existing.TenantId),
            Sanitize(existing.MemberId),
            existing.EventType,
            existing.OccurredAt,
            detection);
    }

    private static string Sanitize(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);
}
