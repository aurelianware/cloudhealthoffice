using CloudHealthOffice.Events;

namespace AccumulatorService.Services;

/// <summary>
/// Publishes outbound accumulator events to Kafka.
///
/// TODO(addendum-a): claims-service → accumulator-service is currently Kafka via
/// Confluent.Kafka, consistent with the rest of the claims pipeline. When the
/// Phase 1/2 boundary brings the IMessageBus abstraction (Service Bus-backed) this
/// flow is a candidate for evaluation — though claim events are pub-sub fan-out
/// (accumulators, risk adjustment, analytics, condition-service) and Kafka may
/// remain the right choice once formalized. Not a problem for this PR.
/// </summary>
public interface IAccumulatorEventPublisher
{
    Task PublishAdjustedAsync(AccumulatorAdjustedEvent evt, CancellationToken ct = default);
    Task PublishOrphanAsync(OrphanAccumulatorClaimEvent evt, CancellationToken ct = default);
}
