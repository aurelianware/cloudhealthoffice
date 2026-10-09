# Appeals event outbox

appeals-service publishes every appeal change to Kafka topic
`appeal.status-changed.v1` (partition key `appealId`; headers `tenant-id`,
`event-type`, `event-version`, `event-id`). Appeal and grievance timelines and
notices are regulated, so a consumer must never miss one of these events. The
service therefore uses a **transactional outbox**. Before this change, a publish
that ran while Kafka was down or not yet started returned silently, and the
event was lost.

## How it works

**Writing.** Each repository mutation (create, transition, close, note,
attachment, acknowledgment, reviewer assignment, overdue observation, deadline
extension, 275 ingress, legacy-status migration) appends the event's wire
payload to `Appeal.Outbox`. That is an array on the appeal document itself,
written in the **same single-document update** as the change:

| Store  | Write                                                                    |
|--------|--------------------------------------------------------------------------|
| Mongo  | `$push` to `Outbox` in the same `FindOneAndUpdate` / `InsertOne` as the change |
| Cosmos | added to the same ETag-pinned `CreateItem` / `ReplaceItem` as the change |

Why the outbox lives on the document, and not in a separate collection:

- Mongo multi-document transactions need a replica set. Our dev deployment and
  `infrastructure/k8s/mongodb-deployment.yaml` run a standalone `mongod`, which
  has none.
- A single-document write is atomic on a standalone `mongod` and in Cosmos alike.
- In Cosmos, a single-document write is a stronger guarantee than a
  same-partition transactional batch.
- The state change and its event commit together or not at all. A crash between
  the write and the publish therefore cannot lose the event.

The payload is built by the field-whitelisted builders in
`AppealEventPublisher`, so encrypted-at-rest fields never reach it. Its
`eventId` is the audit row's `EventId`. That is the client-supplied idempotency
key when the client sends one, and the same id is sent as the `event-id` header.

**Relay.** `AppealOutboxDispatcher` publishes pending entries:

- **Inline:** right after the request that made the change (low latency).
- **Background sweep:** every `PollInterval`. This is the delivery guarantee.

A per-appeal lease (`OutboxLeaseOwner` / `OutboxLeaseUntil`) keeps replicas from
publishing the same appeal at once. A lease left by a crashed pod lapses after
`LeaseDuration`.

- **Order:** entries go out in write order. A failed entry blocks the later
  entries of the same appeal until it is sent or dead-lettered. Other appeals
  are not affected.
- **At-least-once:** an entry is marked `Sent` only after the broker
  acknowledges it. A crash between the ack and the mark republishes the
  identical message, so consumers must de-duplicate on `eventId`.
- **Transient failures** (broker unreachable, timeouts, all brokers down): the
  entry stays `Pending` and uses no attempt. The whole relay backs off
  exponentially (`InitialBackoff`, doubling, up to `MaxBackoff`). An outage
  therefore never dead-letters events.
- **Non-transient failures** (the message itself is rejected, for example
  `MsgSizeTooLarge` or an authorization error): `Attempts` increases and the
  entry retries with exponential backoff. After `MaxAttempts` it becomes
  `DeadLettered`. This writes an error log and increments
  `cho.appeals.outbox.outcomes.total{cho.outcome="dead_lettered"}`, and later
  entries of the appeal proceed.
- **Pruning:** `Sent` and `Skipped` entries are pruned after `SentRetention`.
  `DeadLettered` entries stay until they are replayed.

The metric `cho.appeals.outbox.outcomes.total` has these labels:

- `cho.outcome`: `published`, `failed`, `dead_lettered` or `skipped`
- `cho.event_type`
- `cho.transient`

It never carries tenant, appeal or member identity.

## Configuration

| Key | Default | Meaning |
|-----|---------|---------|
| `Kafka:BootstrapServers` | unset | Unset means Kafka publishing is **disabled by configuration** (a supported mode) |
| `AppealOutbox:SkipWhenKafkaDisabled` | `false` | Kafka disabled: `false` keeps events `Pending`, so enabling Kafka later delivers the backlog; `true` marks them `Skipped` |
| `AppealOutbox:DispatchInline` | `true` | Publish right after each request |
| `AppealOutbox:Enabled` | `true` | Run the background sweep |
| `AppealOutbox:PollInterval` | `00:00:02` | Time between sweeps |
| `AppealOutbox:BatchSize` | `50` | Appeals visited per sweep |
| `AppealOutbox:MaxAttempts` | `10` | Non-transient failures before an entry is dead-lettered |
| `AppealOutbox:InitialBackoff` / `MaxBackoff` | `00:00:01` / `00:05:00` | Exponential retry and pause bounds |
| `AppealOutbox:LeaseDuration` | `00:00:30` | Per-appeal dispatch lease |
| `AppealOutbox:SentRetention` | `1.00:00:00` | How long sent and skipped entries are kept |

In both Kafka-disabled modes, appeal changes and audit rows are written as usual.
That includes the legacy-status migration
(`AppealStatusMigrationHostedService`), which writes its `AppealStatusMigrated`
events into the outbox in the same update as the status rewrite. The migration
no longer waits for the Kafka producer to start.

If Kafka is configured but the producer fails to build, events stay `Pending`
until a restart succeeds.

## Inspecting the outbox

Mongo (element names are the PascalCase class-map names):

```js
// Appeals with undelivered events
db.Appeals.find({ "Outbox.Status": "Pending" }, { TenantId: 1, Outbox: 1 })
// Dead letters
db.Appeals.find({ "Outbox.Status": "DeadLettered" },
  { TenantId: 1, "Outbox.$": 1 })
```

Cosmos (enum values are camelCase):

```sql
SELECT c.tenantId, c.id, o.eventId, o.eventType, o.attempts, o.lastError
FROM c JOIN o IN c.outbox WHERE o.status = 'deadLettered'
```

## Replaying dead-lettered events

1. Find the cause in the log line
   `Appeal outbox event dead-lettered after N attempts ...` or in
   `Outbox[].LastError`, and fix it (for example, topic ACLs or the broker's
   `message.max.bytes`).
2. Requeue as a tenant admin (`appeals:admin` or `platform:admin`). This sets the
   entries back to `Pending` with zero attempts and nudges the relay:

   ```http
   POST /api/appeals/{appealId}/outbox/replay              # every dead letter of the appeal
   POST /api/appeals/{appealId}/outbox/replay?eventId={id} # one event
   ```

   The response is `{ "requeued": <count> }`.

   Without the API, for example across many appeals, run this in Mongo:

   ```js
   db.Appeals.updateMany(
     { "Outbox.Status": "DeadLettered" },
     { $set: { "Outbox.$[e].Status": "Pending", "Outbox.$[e].Attempts": 0,
               "Outbox.$[e].NextAttemptAt": null, "Outbox.$[e].CompletedAt": null } },
     { arrayFilters: [ { "e.Status": "DeadLettered" } ] })
   ```

   On Cosmos, use the API, or patch the entries' `status` to `pending` and
   `attempts` to `0` through the SDK.

A replayed event keeps its original `eventId`. It is published after the
appeal's later events that already went out, so consumers that need order should
use `occurredAt` together with `eventId`.
