using ConsentService.Models;

namespace ConsentService.Services;

/// <summary>
/// Emits <c>ConsentStatusChanged</c> events to Kafka. The DB is source of
/// truth — a Kafka failure is logged but never propagated.
/// </summary>
public interface IConsentEventPublisher
{
    /// <summary>
    /// Publish a <c>ConsentStatusChanged</c> event. <paramref name="fromStatus"/>
    /// is <c>null</c> for the genesis event (creation into <c>Draft</c>).
    /// Safe to call when Kafka is unavailable — failures are logged but never
    /// propagated.
    /// </summary>
    Task PublishStatusChangedAsync(
        Consent consent,
        ConsentStatus? fromStatus,
        ConsentStatus toStatus,
        string actor,
        string? correlationId,
        CancellationToken ct = default);
}
