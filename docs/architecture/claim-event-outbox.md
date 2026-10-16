# Claim-event outbox (`claims.finalized.v1`)

**Status:** implemented in claims-service. It covers `claims.finalized.v1` only.

## Why

claims-service published `claims.finalized.v1` right after the database write
that finalized a claim, and logged and dropped any produce failure. A broker
outage, a timeout, or a crash between the write and the publish meant
accumulator-service never heard about that claim. Nothing repaired it.

## How it works

1. **Same write.** The event is built when the claim changes state and
   appended to `Claim.PendingClaimEvents` (`ClaimEventOutboxItem`: event id,
   topic, key, serialized payload, headers, due time). It goes into the same
   single-document write as the status change. That write is one of:
   - the finalize `UpdateAsync`;
   - the void's `MarkVoidedProjectionAsync`;
   - the examiner resolve / accumulator re-run lock-fenced replace;
   - `PUT /api/claims/{id}/status`, which sets terminal statuses.

   A single-document write is atomic on MongoDB and on Azure Cosmos DB for
   MongoDB. No multi-document transaction is used.
2. **Publish.** Right after the write the request drains the claim's pending
   events, oldest first, through the shared `ChoKafkaProducer`. Each event
   removed with `$pull` by event id has been acknowledged with `acks=all`.
3. **Retry.** `ClaimEventOutboxDispatcher` polls every 10 s
   (`Claims:EventOutbox`):
   - it uses the sparse multikey index `claim_event_outbox_due` to find
     claims with a due event;
   - it drains each claim and reschedules a failed event with exponential
     backoff, from 5 s up to 10 min;
   - it leaves a new event to its writer for 30 s;
   - it logs an error while the oldest event is older than 15 min;
   - its metrics are `cho.claims.event_outbox.attempts.total` and
     `cho.claims.event_outbox.oldest_age`.
4. **Order.** A claim's events are published oldest first. The first one Kafka
   does not acknowledge holds back the rest, so `Paid` never arrives after
   `Reversed`.

## Guarantees

- **At least once, stable id.** A crash after the acknowledgement and before
  the `$pull`, or two drainers at once, publishes an event again with the same
  `eventId` and payload. Consumers deduplicate:
  - accumulator-service is idempotent per claim; a redelivery is `Duplicate`;
  - work-management-service ignores an event whose `SourceVersion` is not
    newer.
- **Not configured, not queued.** Without `Kafka:BootstrapServers` nothing is
  queued or published, as before.

## Known limits

- **`PUT /api/claims/{id}/status`.** The status is written by
  `TryTransitionStatusAsync` and the event by the `UpdateAsync` that follows.
  A crash between those two writes loses the event. This applies only to the
  terminal status that endpoint can set (Voided); finalize and void use the
  atomic paths above.
- **Cosmos SDK backend** (`Database:Provider=CosmosDb`, to be removed). The
  event list is changed by an ETag-fenced read-modify-patch.
- **Tenant-scoped databases** (`MongoDb:UseTenantScoping`). As with the
  accumulator outbox, the dispatcher scans only the base claims database.
- **Other claim topics.** These are not in the outbox:
  - `claims.pended.v1` is advisory, for claims-examiner-service;
  - `claims.work.v1` is repaired by work-management-service's reconcile
    against claims-service.

Both now use the shared producer, but a failed publish of either is still
logged and dropped.

## Not connected: encounter-submission-service

encounter-submission-service consumes `adjudication-completed`
(`Kafka:AdjudicationCompletedTopic`). **Nothing in the repository produces that
topic.** The service does not miss claim events; it is not fed at all. Fixing
that is a contract decision: claims-service could produce
`adjudication-completed`, or the service could consume `claims.finalized.v1`.

## Kafka producers still built per service

`CloudHealthOffice.Events.Kafka.ChoKafkaProducer` (idempotence on,
`acks=all`, bounded delivery timeout) is the shared producer. These services
still build their own `ProducerBuilder` and are listed for a later pass:

| Service | File | Idempotence |
|---|---|---|
| appeals-service | `Services/AppealEventPublisher.cs` | on |
| personal-representative-service | `Services/PersonalRepEventPublisher.cs` | on |
| consent-service | `Services/ConsentEventPublisher.cs` | on |
| accumulator-service | `Services/KafkaAccumulatorEventPublisher.cs` | on |
| rfai-service | `Services/KafkaProducerService.cs` | **off** |
| encounter-submission-service | `KafkaConsumers/AdjudicationCompletedConsumer.cs`, `Workers/EncounterSubmissionWorker.cs` | **off** |

librdkafka turns on `acks=all` whenever idempotence is enabled, so the
producers marked "on" already wait for every in-sync replica. The two marked
"off" use the client default, `acks=all` in Confluent.Kafka 2.x, but without
idempotence a retry inside the client can duplicate or reorder messages.
