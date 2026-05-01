# Shared `IMessageBus`

`IMessageBus` is the Cloud Health Office async-messaging abstraction. One
interface, three interchangeable backends, lives in
[`CloudHealthOffice.Infrastructure.Messaging`](../../src/services/shared/CloudHealthOffice.Infrastructure/Messaging/).
Production runs on Azure Service Bus; dev and test run on an in-process
channel.

## What this is — and isn't

`IMessageBus` covers **durable async work dispatch** between CHO services:
work queues, pub-sub topics, and scheduled delivery. It is deliberately
**not** a Kafka facade. High-throughput streaming analytics
(accumulator-service, authorization-service,
rfai-service, enrollment-import-service) remain on their own dedicated
Kafka clients; those pipelines need Kafka-specific semantics
(partitioning, compaction, change-feed replay) that don't belong on a
cross-backend interface.

## The three canonical patterns

### Work queue (competing consumers)

One queue, multiple subscribers, each message delivered exactly once
across the group. This is what eligibility-service and idcard-service
both use today.

```csharp
// Producer
await bus.SendAsync("batch-eligibility", new BatchQueueMessage(tenantId, jobId));

// Consumer (registered as a hosted service)
await using var sub = bus.Subscribe<BatchQueueMessage>(
    "batch-eligibility",
    async (msg, ctx, ct) => await handler.ProcessAsync(msg, ct));
await sub.StartAsync(stoppingToken);
```

### Pub-sub topic (fan-out)

One topic, multiple named subscriptions, each subscription gets its own
copy of every message. Pass `SubscriptionName` to
[`SubscriptionOptions`](../../src/services/shared/CloudHealthOffice.Infrastructure/Messaging/IMessageBus.cs).

```csharp
bus.Subscribe<MemberEligibilityChanged>(
    "member-events",
    handler,
    new SubscriptionOptions(SubscriptionName: "idcard-service"));
```

### Scheduled delivery

`ScheduleAsync` delivers no earlier than the requested timestamp.
Backend-specific resolution — Service Bus honours to the second; the
in-memory bus uses a timer and is suitable only for tests.

```csharp
await bus.ScheduleAsync(
    "reminders",
    reminder,
    DateTimeOffset.UtcNow.AddHours(24));
```

## Naming convention

`{purpose}` for cross-environment resources where the Service Bus
namespace is already per-environment. Example production names used
today:

| Queue                    | Owner                | Purpose                                    |
| ------------------------ | -------------------- | ------------------------------------------ |
| `batch-eligibility`      | eligibility-service  | Bulk 270/271 job dispatch                  |
| `qnxt-idcard-requests`   | idcard-service       | QNXT mirror of CHO-issued ID cards         |

Do **not** rename existing queues. These names appear in live
`ServiceBusSender` calls and in the queue-provisioning script; a rename
is a deployment-breaking change, not a code refactor. New queues should
follow `{service}-{purpose}` (lowercase, hyphenated) so routing and
ownership are both obvious from the name.

## Idempotency and deduplication

`SendOptions.MessageId` maps directly onto Service Bus native duplicate
detection when the queue is provisioned with
`RequiresDuplicateDetection=true`. Within the detection window
(`MessagingOptions.DuplicateDetectionWindow`, default 1 hour) a repeat
send with the same `MessageId` is dropped by the broker.

Tradeoffs to know before you rely on it:

- **The window is a wall-clock window, not per-consumer.** A replay
  that straddles the window gets delivered twice; handlers must still be
  idempotent at the application layer. Dedup is a defence in depth, not
  a correctness guarantee.
- **Queue provisioning is infra, not code.** The bus never creates or
  modifies queues. A `MessageId` set against a queue without dedup
  enabled does nothing. Use
  [`scripts/azure/provision-servicebus-queues.sh`](../../scripts/azure/provision-servicebus-queues.sh)
  to provision.
- **In-memory emulates this for tests.** `InMemoryMessageBus`
  remembers `MessageId`s within the same window so the contract test
  passes identically on both backends.

## Configuration

Bind from the `Messaging` section:

```json
{
  "Messaging": {
    "Backend": "Auto",
    "ServiceBusConnectionString": null
  }
}
```

Backend resolution:

| `Backend`    | Result                                                       |
| ------------ | ------------------------------------------------------------ |
| `Auto` (dev) | `InMemory`                                                   |
| `Auto` (prod) | `ServiceBus` if `ServiceBusConnectionString` is set, else `InMemory` + warning |
| `ServiceBus` | Forced; throws at startup if no connection string            |
| `InMemory`   | Forced                                                       |
| `Null`       | Forced no-op (test scenarios that don't care about messaging) |

`AddChoMessaging` logs one line at startup stating the chosen backend and
the reason, e.g. `IMessageBus=ServiceBus (Auto; ConnectionString
configured; env=Production)`.

### Legacy keys

Two legacy connection-string keys are honoured as fallbacks:

- `BatchEligibility:ServiceBus:ConnectionString`
- `IdCard:QnxtMirror:ServiceBusConnectionString`

If either is read, a single startup warning names the deprecated key and
the canonical replacement. These fallbacks will be removed one release
after this change lands; track the follow-up in issue
**"Remove deprecated Messaging config key fallbacks"**.

## Migration notes for new services

If you're adding Service Bus to a new service:

1. Add `builder.Services.AddChoMessaging(builder.Configuration,
   builder.Environment);` to `Program.cs`. `IMessageBus` is a singleton
   afterwards.
2. **Do not** register your own `ServiceBusClient` — the bus owns the
   client lifecycle.
3. Do not add `Azure.Messaging.ServiceBus` to your service's `.csproj`.
   It lives in `CloudHealthOffice.Infrastructure` and you get it
   transitively.
4. Add an entry to
   [`provision-servicebus-queues.sh`](../../scripts/azure/provision-servicebus-queues.sh)
   for your queue name so staging/prod can be provisioned with the
   correct dedup + DLQ settings.
5. If you want to consume, use `SubscriptionHostedService` rather than
   rolling your own `BackgroundService` wrapper.

## When you should NOT use `IMessageBus`

| Need                                             | Use                                |
| ------------------------------------------------ | ---------------------------------- |
| High-throughput event streaming / analytics      | **Kafka** (keep existing clients)  |
| Request/response with a latency budget           | **HTTP** (or gRPC if warranted)    |
| Replay from the point-in-time source of truth    | **Cosmos Change Feed**             |
| In-process work scheduling inside one service    | `Task.Run` / `BackgroundService`   |

Quick decision tree:

```
  Does the consumer live in a different service?  — no  → just call it
             |
             yes
             |
  Do you need event replay / stream analytics?     — yes → Kafka
             |
             no
             |
  Is the consumer synchronous to the request?     — yes → HTTP
             |
             no
             |
                                                          → IMessageBus
```

## Observability

Every send and receive is a span under the `CloudHealthOffice`
`ActivitySource`. Producer spans are `ActivityKind.Producer`; consumer
spans are `ActivityKind.Consumer`. W3C trace context is injected as a
`traceparent` application property on send and restored on receive so
cross-service traces link up end-to-end.

Azure Service Bus emits its own activities under
`Azure.Messaging.ServiceBus.*`. `AddChoObservability` subscribes to
those, so the SDK's own send/receive spans flow into the same trace.

PHI scrubbing is global via `PhiScrubbingSpanProcessor`. It strips
prohibited attributes on export, but the convention still stands:
prefer not to put raw member IDs or claim IDs in span tags in the first
place. Hash them via
[`ChoActivitySource.HashIdentifier`](../../src/services/shared/CloudHealthOffice.Infrastructure/Observability/ChoActivitySource.cs)
if you need to trace through.
