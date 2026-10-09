# Appeals event outbox

appeals-service publishes every appeal change to Kafka topic
`appeal.status-changed.v1`. Appeal and grievance timelines and notices are
regulated, so a consumer must never miss one of these events. The service
therefore uses a **transactional outbox**. Before this change, a publish that ran
while Kafka was down or not yet started returned silently, and the event was
lost.

Each message carries:

- **Partition key:** `appealId`.
- **Headers:** `tenant-id`, `event-type`, `event-version`, `event-id` and
  `event-sequence`.

## How it works

### Writing

Each repository mutation appends the event's wire payload to `Appeal.Outbox`,
an array on the appeal document itself. The append happens in the **same
single-document write** as the change. This covers create, transition, close,
note, attachment, acknowledgment, reviewer assignment, overdue observation,
deadline extension, 275 ingress and the legacy-status migration.

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
- The state change and its event commit together or not at all.

A repository refuses a mutating call whose audit event carries no outbox
message. An appeal change can therefore never silently skip its event.

The payload is built by the field-whitelisted builders in
`AppealEventPublisher`, so encrypted-at-rest fields never reach it.

### Identity, idempotency and order

Each outbox row has these identifiers:

| Field | What it is |
|-------|------------|
| `Id` | Server-generated row id. Every relay update matches on it, plus `Status == Pending` and the lease owner. |
| `IdempotencyKey` | The audit row's `EventId`. This is the client's `eventId` when the request supplied one. |
| `EventId` (wire) | `UUIDv5(tenantId:appealId:IdempotencyKey)`. It is the payload `eventId` and the `event-id` header. **Consumers de-duplicate on it.** It is deterministic, so a retry yields the same id. Because it is namespaced, a client key reused on another appeal or tenant never collides. |
| `Sequence` | The per-appeal publish number (1, 2, 3, ...). It is the payload `sequence` and the `event-sequence` header. **Consumers apply last-write-wins by `(appealId, sequence)`.** |

How these behave:

- **Idempotent writes.** Requests carry an `eventId`. Note, attachment,
  acknowledgment and reviewer-assignment requests are idempotent on it: when the
  appeal's outbox already holds that key, the call is a replay. It returns the
  stored appeal and appends nothing.
  - The repositories re-check this inside the write, so concurrent retries are
    caught too. Mongo puts `Outbox.IdempotencyKey $ne key` in the update filter.
    Cosmos checks inside the ETag-pinned read-modify-replace.
  - The replay window is as long as the entry is retained (see Bounds).
- **Sequence assignment.** A sequence is assigned in write order by the lease
  holder, just before an entry's first publish attempt. The write is conditional
  on the appeal's counter being at `sequence - 1`.
  - Redeliveries and replays keep the original sequence. A replayed dead letter
    therefore carries a lower sequence than the events that overtook it, and
    last-write-wins consumers correctly ignore it.

### Relay

`AppealOutboxDispatcher` relays pending entries in two ways:

- **Inline nudge:** after each request. It is fire-and-forget, so a Kafka timeout
  never adds to request latency. It is skipped while the relay is paused.
- **Background sweep:** every `PollInterval`. This is the delivery guarantee.

The rules it follows:

- **Lease.** A per-appeal lease stops two replicas publishing the same appeal
  at once.
  - The lease is renewed before every entry. If renewal fails, the run stops.
  - Every outcome write is conditioned on the lease, the row `Id` and
    `Status == Pending`. A stale holder therefore cannot overwrite the new
    holder's results.
  - A lease left by a crashed pod lapses after `LeaseDuration`.
- **Order.** Entries go out in write order. A failed entry blocks the later
  entries of the same appeal until it is sent or dead-lettered.
- **At-least-once.** An entry is marked `Sent` only after the broker
  acknowledges it. A crash in between republishes the identical message (same
  `eventId` and `sequence`).
- **Transient failures:** a broker outage, timeouts, an unknown topic, a
  topic or cluster authorization failure, SASL authentication failure, or a fatal
  producer error.
  - The entry stays `Pending` and uses no attempt.
  - The whole relay backs off exponentially. Such errors hit every event alike,
    so they never dead-letter anything.
  - A fatal producer error (`Error.IsFatal`) also rebuilds the producer.
- **Non-transient failures:** the message itself is rejected (for example
  `MsgSizeTooLarge`). The entry retries with exponential backoff. After
  `MaxAttempts` it becomes `DeadLettered`, writing an error log and the
  `dead_lettered` metric, and later entries proceed.

### Bounds: no unbounded growth

Every visit to an appeal enforces these limits, including in Kafka-disabled mode
(hourly housekeeping):

| Limit | Effect |
|-------|--------|
| `MaxPendingAge` (7 days) | Older pending entries are dead-lettered as `expired`. |
| `MaxPendingPerAppeal` (100) | The oldest pending entries beyond the cap are dead-lettered as `overflow`. |
| `SentRetention` (1 day), `MaxCompletedPerAppeal` (25) | `Sent` / `Skipped` entries beyond these are pruned. |
| `DeadLetterRetention` (30 days), `MaxDeadLetteredPerAppeal` (50) | Dead letters beyond these are pruned (warning log + `dead_letter_pruned` metric). The audit trail (AppealEvents) still records the change. |

Worst case, an appeal holds 100 + 25 + 50 entries. At roughly 1–1.5 KB each,
that is under 300 KB, far below Cosmos's 2 MB item limit.

### Sweep queries

**Mongo.** Element names are the PascalCase class-map names.

- The sweep filters `{"Outbox.Status": "Pending"}` at top level, next to an
  `$elemMatch` on due time. This lets the planner use the partial index
  `ix_outbox_pending`; a test asserts IXSCAN with no COLLSCAN.
- Dead-letter retention and bulk replay use the partial index
  `ix_outbox_dead_lettered`.

**Cosmos.** Every write recomputes these top-level numbers from the outbox:

- `outboxNextDueAt`: epoch ms of the earliest due pending entry or dead-letter
  expiry; null when there is nothing to do.
- `outboxLeaseUntilMs`
- `outboxPendingCount`
- `outboxOldestPendingAt`
- `outboxDeadLetteredCount`

The cross-partition sweep runs
`WHERE c.outboxNextDueAt <= @now AND lease free ORDER BY c.outboxNextDueAt`.
Appeals whose entries are only backing off are therefore never returned and
cannot starve due ones. A lease is not taken, and nothing is written, when
nothing is due.

## Observability and alerts

| Metric | Labels | Meaning |
|--------|--------|---------|
| `cho.appeals.outbox.outcomes.total` | `cho.outcome` = `published`, `failed`, `dead_lettered`, `expired`, `overflow`, `skipped`, `dead_letter_pruned`; `cho.event_type`; `cho.transient` | Relay outcomes |
| `cho.appeals.outbox.pending` (gauge) | — | Unpublished events, all tenants |
| `cho.appeals.outbox.oldest_pending_age` (gauge, s) | — | Age of the oldest unpublished event |

No metric carries tenant, appeal or member identity.

Suggested alerts:

- `cho_appeals_outbox_oldest_pending_age > 600` for 10 minutes. Consumers are
  behind on regulated events.
- `increase(cho_appeals_outbox_outcomes_total{cho_outcome=~"dead_lettered|expired|overflow"}[15m]) > 0`.
  An event needs a replay.
- `increase(cho_appeals_outbox_outcomes_total{cho_outcome="dead_letter_pruned"}[1d]) > 0`.
  Dead letters aged out without being replayed.

## Configuration

| Key | Default | Meaning |
|-----|---------|---------|
| `Kafka:BootstrapServers` | unset | Unset means Kafka publishing is **disabled by configuration** (a supported mode) |
| `AppealOutbox:SkipWhenKafkaDisabled` | `false` | Kafka disabled: `false` keeps events `Pending` (delivered once Kafka is configured, subject to `MaxPendingAge`); `true` marks them `Skipped` |
| `AppealOutbox:DispatchInline` | `true` | Fire-and-forget publish after each request |
| `AppealOutbox:Enabled` | `true` | Run the background sweep |
| `AppealOutbox:PollInterval` | `00:00:02` | Time between sweeps while publishing |
| `AppealOutbox:MaintenanceInterval` | `01:00:00` | Housekeeping interval while publishing is impossible |
| `AppealOutbox:StatsInterval` | `00:01:00` | Gauge refresh interval |
| `AppealOutbox:BatchSize` | `50` | Appeals visited per sweep |
| `AppealOutbox:MaxAttempts` | `10` | Non-transient failures before an entry is dead-lettered |
| `AppealOutbox:InitialBackoff` / `MaxBackoff` | `00:00:01` / `00:05:00` | Exponential retry and pause bounds |
| `AppealOutbox:LeaseDuration` | `00:00:30` | Per-appeal dispatch lease |
| `AppealOutbox:MaxPendingAge` / `MaxPendingPerAppeal` | `7.00:00:00` / `100` | Pending bounds |
| `AppealOutbox:SentRetention` / `MaxCompletedPerAppeal` | `1.00:00:00` / `25` | Completed-entry bounds |
| `AppealOutbox:DeadLetterRetention` / `MaxDeadLetteredPerAppeal` | `30.00:00:00` / `50` | Dead-letter bounds |

**Shipped k8s manifest.**
`src/services/appeals-service/k8s/appeals-service-deployment.yaml` sets
`AppealOutbox__SkipWhenKafkaDisabled: "true"`. No broker is configured there yet
(`kafka-secret` `bootstrapServers` is empty). With retain mode, every appeal
would accumulate pending events until they expired.

To change it when a broker is ready:

- If the backlog written while Kafka was off must be delivered, flip the setting
  to `"false"` **before** populating `bootstrapServers`.
- Once a broker is configured, leave it `"false"`.

**Kafka-disabled behavior.** In both Kafka-disabled modes, appeal changes and
audit rows are written as usual. That includes the legacy-status migration
(`AppealStatusMigrationHostedService`), which writes its `AppealStatusMigrated`
events into the outbox in the same update as the status rewrite. The migration
does not wait for the Kafka producer.

## Inspecting the outbox

Mongo:

```js
db.Appeals.find({ "Outbox.Status": "Pending" }, { TenantId: 1, Outbox: 1 })
db.Appeals.find({ "Outbox.Status": "DeadLettered" }, { TenantId: 1, "Outbox.$": 1 })
```

Cosmos:

```sql
SELECT c.tenantId, c.id, o.eventId, o.eventType, o.attempts, o.lastError
FROM c JOIN o IN c.outbox WHERE o.status = 'deadLettered'
```

## Replaying dead-lettered events

1. Find the cause in the error log
   (`Appeal outbox event dead-lettered ...`) or in `Outbox[].LastError`, and fix
   it.
2. Requeue the events. Requeued entries go back to `Pending` with zero attempts,
   keep their `eventId` and `sequence`, and the relay publishes them.

| Scope | Request | Permission |
|-------|---------|------------|
| One appeal (all, or one event by wire `eventId` or client key) | `POST /api/appeals/{appealId}/outbox/replay[?eventId=]` | `appeals:admin` or `platform:admin` |
| Every appeal in the caller's tenant | `POST /api/appeals/outbox/replay` | `appeals:admin` or `platform:admin` |
| Every tenant | `POST /api/appeals/outbox/replay-all-tenants` | `platform:admin` only (a tenant admin's `*:*` never reaches `platform:*`) |

Every replay endpoint responds with `{ "requeued": <count> }` and logs the
actor.

Replay before `DeadLetterRetention` runs out. After that, the change exists
only in the audit trail.

## Limitations

- The relay polls. It does not use the Cosmos change feed or Mongo change streams.
- The idempotency replay window for a client `eventId` is the entry's retention
  in the outbox. A retry after the entry was pruned is treated as a new change.
  The audit row is still de-duplicated on `EventId`.
- Audit rows (the AppealEvents collection/container) keep their existing
  non-atomic append. The outbox covers Kafka events.
