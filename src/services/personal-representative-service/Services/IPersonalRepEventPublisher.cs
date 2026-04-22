using PersonalRepresentativeService.Models;

namespace PersonalRepresentativeService.Services;

/// <summary>
/// Emits <c>PersonalRepStatusChanged</c> and association events to Kafka.
/// The DB is source of truth — a Kafka failure is logged but never
/// propagated.
/// </summary>
public interface IPersonalRepEventPublisher
{
    /// <summary>
    /// Publish a <c>PersonalRepStatusChanged</c> event.
    /// <paramref name="fromStatus"/> is <c>null</c> for the genesis event
    /// (creation into <c>Draft</c>). Safe to call when Kafka is
    /// unavailable — failures are logged but never propagated.
    /// </summary>
    Task PublishStatusChangedAsync(
        PersonalRepresentative rep,
        PersonalRepStatus? fromStatus,
        PersonalRepStatus toStatus,
        IReadOnlyList<string> associatedMemberIds,
        string actor,
        string? correlationId,
        CancellationToken ct = default);

    /// <summary>
    /// Publish an association-added or association-removed event. No
    /// encrypted rep fields appear on the payload — the event carries
    /// <c>personalRepId</c>, <c>memberId</c>, <c>credentialType</c> only.
    /// </summary>
    Task PublishAssociationChangedAsync(
        PersonalRepresentative rep,
        PersonalRepAssociation association,
        PersonalRepEventType eventType,
        string actor,
        string? correlationId,
        CancellationToken ct = default);
}
