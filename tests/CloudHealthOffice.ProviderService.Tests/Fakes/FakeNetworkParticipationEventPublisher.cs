using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="INetworkParticipationEventPublisher"/> for
/// service tests. Captures every published event and supports forced
/// failure via <see cref="ThrowOnPublish"/> to exercise the
/// best-effort emission path in the backfill service.
/// </summary>
public sealed class FakeNetworkParticipationEventPublisher : INetworkParticipationEventPublisher
{
    public List<NetworkParticipationEvent> Events { get; } = new();

    public bool ThrowOnPublish { get; set; }

    public Task<NetworkParticipationEvent> PublishPanelGatingBackfilledAsync(
        string tenantId,
        string providerId,
        int participationIndex,
        string? planId,
        string? networkId,
        LineOfBusiness lineOfBusiness,
        string backfillRunId,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        if (ThrowOnPublish)
            throw new InvalidOperationException("Simulated event publication failure");

        var existing = Events.FirstOrDefault(e =>
            e.TenantId == tenantId
            && e.ProviderId == providerId
            && e.ParticipationIndex == participationIndex
            && e.BackfillRunId == backfillRunId);
        if (existing != null) return Task.FromResult(existing);

        var evt = new NetworkParticipationEvent
        {
            EventId = NetworkParticipationEvent.BuildBackfilledEventId(
                providerId, participationIndex, backfillRunId),
            EventType = NetworkParticipationEventType.PanelGatingBackfilled,
            TenantId = tenantId,
            ProviderId = providerId,
            ParticipationIndex = participationIndex,
            PlanId = planId,
            NetworkId = networkId,
            LineOfBusiness = lineOfBusiness,
            BackfillRunId = backfillRunId,
            ActorId = actorId,
            CorrelationId = correlationId,
            PartitionKey = NetworkParticipationEvent.BuildPartitionKey(tenantId, providerId),
            Version = Events.Count(e => e.TenantId == tenantId && e.ProviderId == providerId) + 1,
            OccurredAt = DateTime.UtcNow,
        };
        Events.Add(evt);
        return Task.FromResult(evt);
    }
}
