# ADR 002: Apache Kafka vs NATS for EDI Event Streaming

## Status

**Accepted**

## Context

Cloud Health Office requires an event streaming platform to replace Azure Service Bus for X12 EDI message routing. Key requirements:

- High throughput (10,000+ messages/day)
- Message durability and replay capability
- HIPAA-oriented data handling controls
- Multi-consumer support (fan-out)
- Dead-letter queue functionality

Candidates evaluated:
1. **Apache Kafka** - Distributed event streaming platform
2. **NATS JetStream** - Lightweight messaging with persistence
3. **RabbitMQ** - Traditional message broker
4. **AWS SQS/SNS** - Managed cloud messaging (vendor-specific)

## Decision

We will use **Apache Kafka** for event streaming in the X12 EDI pipeline.

## Rationale

### Kafka Advantages

1. **Proven at Scale**
   - Battle-tested at LinkedIn, Netflix, Uber
   - Handles millions of messages per second
   - Linear horizontal scaling

2. **Durable Message Log**
   - Messages persisted to disk
   - Configurable retention (7-90 days)
   - Offset-based replay capability
   - Essential for HIPAA audit requirements

3. **Consumer Groups**
   - Multiple consumers per topic
   - Load balancing within consumer groups
   - Independent offset tracking
   - Supports both pub-sub and queue patterns

4. **Exactly-Once Semantics**
   - Transactional producers
   - Idempotent delivery
   - Critical for EDI transaction integrity

5. **Schema Management**
   - Schema Registry support
   - Avro/JSON schema validation
   - Schema evolution

6. **Operational Maturity**
   - Strimzi Kubernetes operator
   - Extensive monitoring (JMX, Prometheus)
   - Commercial support options (Confluent)

### Comparison with Alternatives

| Feature | Kafka | NATS JetStream | RabbitMQ |
|---------|-------|----------------|----------|
| Message Durability | ✅ Log-based | ✅ Persistent | ✅ Durable queues |
| Replay Capability | ✅ Offset-based | ⚠️ Limited | ❌ No |
| Throughput | ✅ Very High | ✅ High | ⚠️ Medium |
| Consumer Groups | ✅ Native | ⚠️ Basic | ⚠️ Manual |
| Kubernetes Operator | ✅ Strimzi | ⚠️ Basic | ✅ RabbitMQ Operator |
| Community Size | ✅ Large | ⚠️ Growing | ✅ Large |
| Complexity | ⚠️ Higher | ✅ Lower | ✅ Lower |

### Why Not NATS?

- Smaller community and ecosystem
- Less mature persistence layer (JetStream is newer)
- Limited tooling for operations
- Fewer monitoring integrations

### Why Not RabbitMQ?

- No native message replay
- Complex clustering for HA
- Lower throughput at scale
- Different messaging paradigm (broker vs log)

### Topic Design for EDI

```
Topics:
├── edi-raw-files          # Raw EDI file events (7 days retention)
├── attachments-in         # Processed 275 attachments (30 days)
├── edi-278                # Processed 278 authorizations (30 days)
├── rfai-requests          # 277 RFAI generation requests (7 days)
├── edi-277-outbound       # Generated 277 responses (30 days)
└── dead-letter-queue      # Failed messages (90 days)
```

## Consequences

### Positive

- Reliable message delivery for EDI transactions
- Full audit trail with message replay
- Scalable to 10x current volume
- Strong Kubernetes integration via Strimzi
- Rich ecosystem (Kafka Connect, Streams)

### Negative

- Higher operational complexity than NATS
- Requires ZooKeeper (or KRaft in newer versions)
- More resource-intensive
- Steeper learning curve

### Mitigations

- Use Strimzi operator for simplified management
- Deploy KRaft mode to eliminate ZooKeeper
- Document common operations in runbook
- Implement comprehensive monitoring/alerting

## Implementation Notes

### Kafka Deployment

```yaml
# Using Strimzi Kafka Operator
apiVersion: kafka.strimzi.io/v1beta2
kind: Kafka
metadata:
  name: cloudhealthoffice-kafka
spec:
  kafka:
    replicas: 3
    config:
      min.insync.replicas: 2
      auto.create.topics.enable: false
    storage:
      type: persistent-claim
      size: 50Gi
```

### Topic Configuration

```yaml
apiVersion: kafka.strimzi.io/v1beta2
kind: KafkaTopic
metadata:
  name: attachments-in
spec:
  partitions: 3
  replicas: 3
  config:
    retention.ms: 2592000000  # 30 days
    compression.type: lz4
```

## References

- [Apache Kafka Documentation](https://kafka.apache.org/documentation/)
- [Strimzi Kafka Operator](https://strimzi.io/)
- [Kafka vs RabbitMQ](https://www.confluent.io/blog/kafka-vs-rabbitmq/)
- [NATS JetStream](https://docs.nats.io/nats-concepts/jetstream)
