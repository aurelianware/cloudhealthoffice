using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IProviderVerificationEventPublisher"/>. Records
/// every published event and enforces the same idempotency contract as
/// the real Mongo publisher (deterministic <c>EventId</c> on
/// <c>(providerId, verifiedAt)</c>).
/// </summary>
public sealed class FakeProviderVerificationEventPublisher : IProviderVerificationEventPublisher
{
    public List<ProviderVerificationEvent> Events { get; } = new();

    public Task<ProviderVerificationEvent> PublishRefreshedAsync(
        string tenantId,
        string providerId,
        int? integrityScore,
        string? integrityRating,
        DateTimeOffset verifiedAt,
        DateTimeOffset? nextVerificationDue,
        string? actorId,
        string? correlationId,
        CancellationToken ct = default)
    {
        var eventId = ProviderVerificationEvent.BuildRefreshedEventId(providerId, verifiedAt);
        var existing = Events.FirstOrDefault(e =>
            e.TenantId == tenantId && e.ProviderId == providerId && e.EventId == eventId);
        if (existing != null) return Task.FromResult(existing);

        var evt = new ProviderVerificationEvent
        {
            EventId = eventId,
            EventType = ProviderVerificationEventType.ProviderVerificationRefreshed,
            TenantId = tenantId,
            ProviderId = providerId,
            IntegrityScore = integrityScore,
            IntegrityRating = integrityRating,
            VerifiedAt = verifiedAt,
            NextVerificationDue = nextVerificationDue,
            ActorId = actorId,
            CorrelationId = correlationId,
            Version = Events.Count(e => e.TenantId == tenantId && e.ProviderId == providerId) + 1,
            OccurredAt = DateTime.UtcNow,
        };
        Events.Add(evt);
        return Task.FromResult(evt);
    }
}
