# Argo Workflows Operations Guide

This guide covers day-to-day operations of the Cloud Health Office X12 EDI processing platform on Kubernetes with Argo Workflows.

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         Cloud Health Office Platform                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌─────────────┐     ┌──────────────┐     ┌─────────────────────────────┐   │
│  │   Clearinghouse  │────▶│ Argo Events  │────▶│     Argo Workflows          │   │
│  │    SFTP     │     │  (Sensors)   │     │                             │   │
│  └─────────────┘     └──────────────┘     │  ┌─────────┐ ┌─────────┐   │   │
│                                            │  │x12-275  │ │x12-278  │   │   │
│  ┌─────────────┐     ┌──────────────┐     │  │ ingest  │ │ ingest  │   │   │
│  │   Apache    │◀───▶│   Kafka      │◀───▶│  └─────────┘ └─────────┘   │   │
│  │   Kafka     │     │  Publisher   │     │                             │   │
│  └─────────────┘     └──────────────┘     │  ┌─────────┐ ┌─────────┐   │   │
│                                            │  │x12-277  │ │x12-278  │   │   │
│  ┌─────────────┐                          │  │  rfai   │ │ replay  │   │   │
│  │ S3 / MinIO  │◀─────────────────────────│  └─────────┘ └─────────┘   │   │
│  │ Data Lake   │                          └─────────────────────────────┘   │
│  └─────────────┘                                                            │
│                       ┌──────────────────────────────────────────────────┐  │
│                       │            Monitoring Stack                       │  │
│                       │  Prometheus │ Grafana │ Alertmanager             │  │
│                       └──────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Deployment

### Quick Start

```bash
# Deploy the entire stack using Helm
helm install cloudhealthoffice ./helm/cloudhealthoffice \
  -n cloudhealthoffice \
  --create-namespace \
  -f helm/cloudhealthoffice/values.yaml

# Verify deployment
kubectl get pods -n cloudhealthoffice
kubectl get workflowtemplates -n cloudhealthoffice
kubectl get eventsources -n cloudhealthoffice
kubectl get sensors -n cloudhealthoffice
```

### Scaling Workflows

```bash
# Scale workflow controller parallelism
kubectl patch deployment argo-workflows-controller -n cloudhealthoffice \
  --patch '{"spec":{"template":{"spec":{"containers":[{"name":"controller","args":["--parallelism=20"]}]}}}}'

# Scale Kafka partitions for higher throughput
kubectl apply -f - <<EOF
apiVersion: kafka.strimzi.io/v1beta2
kind: KafkaTopic
metadata:
  name: attachments-in
  namespace: kafka
spec:
  partitions: 6  # Increased from 3
  replicas: 3
EOF
```

## Daily Operations

### Monitoring Workflow Status

```bash
# List recent workflows
kubectl get workflows -n cloudhealthoffice --sort-by=.metadata.creationTimestamp

# Watch workflows in real-time
watch kubectl get workflows -n cloudhealthoffice -l workflows.argoproj.io/completed=false

# Get workflow details
kubectl describe workflow <workflow-name> -n cloudhealthoffice

# View workflow logs
kubectl logs -l workflows.argoproj.io/workflow=<workflow-name> -n cloudhealthoffice --all-containers
```

### Argo Workflows UI

Access the Argo Workflows UI:

```bash
# Port forward to local machine
kubectl port-forward svc/argo-workflows-server 2746:2746 -n cloudhealthoffice

# Open browser: https://localhost:2746
```

### Monitoring Kafka

```bash
# Check consumer group lag
kubectl exec -it kafka-0 -n kafka -- \
  kafka-consumer-groups.sh --bootstrap-server localhost:9092 \
  --describe --all-groups

# List topics
kubectl exec -it kafka-0 -n kafka -- \
  kafka-topics.sh --bootstrap-server localhost:9092 --list

# Check topic details
kubectl exec -it kafka-0 -n kafka -- \
  kafka-topics.sh --bootstrap-server localhost:9092 \
  --describe --topic attachments-in
```

### Checking EDI Processing

```bash
# View recent 275 processing
kubectl get workflows -n cloudhealthoffice -l workflows.argoproj.io/workflow-template=x12-275-ingest

# View recent 278 processing
kubectl get workflows -n cloudhealthoffice -l workflows.argoproj.io/workflow-template=x12-278-ingest

# View 277 RFAI generations
kubectl get workflows -n cloudhealthoffice -l workflows.argoproj.io/workflow-template=x12-277-rfai
```

## Alerting

### Prometheus Alerts

Key alerts configured in `monitoring/prometheus-rules.yaml`:

| Alert | Severity | Description |
|-------|----------|-------------|
| ArgoWorkflowFailed | Critical | Workflow execution failed |
| ArgoWorkflowRunningTooLong | Warning | Workflow exceeds 30 minute timeout |
| KafkaConsumerLagHigh | Warning | Consumer lag > 1000 messages |
| KafkaConsumerLagGrowing | Critical | Consumer lag increasing rapidly |
| DeadLetterQueueNotEmpty | Warning | Failed messages in DLQ |
| X12ParsingErrorsHigh | Warning | High rate of parsing errors |
| SFTPConnectionFailed | Critical | SFTP connectivity issues |
| X12_275ProcessingSLABreach | Critical | 275 processing exceeds 2 min SLA |

### Responding to Alerts

#### ArgoWorkflowFailed

1. Check workflow status:
   ```bash
   kubectl get workflow <name> -n cloudhealthoffice -o yaml
   ```

2. View failed step logs:
   ```bash
   kubectl logs -l workflows.argoproj.io/workflow=<name> --tail=100 -n cloudhealthoffice
   ```

3. Check for common issues:
   - SFTP connectivity
   - Kafka availability
   - Secret expiration
   - Resource constraints

4. Retry if transient:
   ```bash
   argo retry <workflow-name> -n cloudhealthoffice
   ```

#### KafkaConsumerLagHigh

1. Check consumer group status:
   ```bash
   kubectl exec kafka-0 -n kafka -- kafka-consumer-groups.sh \
     --bootstrap-server localhost:9092 --describe --group argo-rfai-processor
   ```

2. Scale workflow processing:
   ```bash
   kubectl patch sensor rfai-sensor -n cloudhealthoffice \
     --type=merge -p '{"spec":{"template":{"parallelism": 5}}}'
   ```

3. Check for failed workflows backing up the queue

#### DeadLetterQueueNotEmpty

1. Inspect DLQ messages:
   ```bash
   kubectl exec kafka-0 -n kafka -- kafka-console-consumer.sh \
     --bootstrap-server localhost:9092 --topic dead-letter-queue \
     --from-beginning --max-messages 10
   ```

2. Investigate failure reasons in message metadata

3. Fix root cause and replay messages:
   ```bash
   ./migration/replay-dlq.sh --topic dead-letter-queue --limit 100
   ```

## Troubleshooting

### Workflow Not Starting

```bash
# Check event source
kubectl get eventsource -n cloudhealthoffice
kubectl describe eventsource sftp-polling -n cloudhealthoffice

# Check sensor
kubectl get sensor -n cloudhealthoffice
kubectl describe sensor sftp-sensor -n cloudhealthoffice

# Check sensor pod logs
kubectl logs -l sensor-name=sftp-sensor -n cloudhealthoffice
```

### SFTP Connectivity Issues

```bash
# Test SFTP connection
kubectl run sftp-test --rm -it --image=cloudhealthoffice/sftp-fetcher \
  --env="SFTP_HOST=sftp.clearinghouse.example.com" \
  --env="SFTP_USERNAME=$(kubectl get secret clearinghouse-sftp-secret -n cloudhealthoffice -o jsonpath='{.data.username}' | base64 -d)" \
  --env="SFTP_PASSWORD=$(kubectl get secret clearinghouse-sftp-secret -n cloudhealthoffice -o jsonpath='{.data.password}' | base64 -d)" \
  -- --list-only --folder /inbound/attachments
```

### X12 Parsing Errors

```bash
# Get parsing error logs
kubectl logs -l step-name=parse-x12-275 -n cloudhealthoffice --tail=100

# Test parser with specific file
kubectl run parser-test --rm -it --image=cloudhealthoffice/x12-parser \
  -- /path/to/file.edi --log-level DEBUG
```

### Kafka Connectivity Issues

```bash
# Test Kafka connectivity
kubectl run kafka-test --rm -it --image=bitnami/kafka:latest \
  --env="KAFKA_BOOTSTRAP_SERVERS=kafka-cluster-kafka-bootstrap.kafka:9092" \
  -- kafka-topics.sh --bootstrap-server kafka-cluster-kafka-bootstrap.kafka:9092 --list
```

## Backup and Recovery

### Workflow Archive

Completed workflows are automatically archived. Query via Argo Server API:

```bash
# List archived workflows
curl -X GET https://localhost:2746/api/v1/archived-workflows \
  -H "Authorization: Bearer $ARGO_TOKEN"
```

### Kafka Data Retention

Topics are configured with retention policies:
- `edi-raw-files`: 7 days
- `attachments-in`: 30 days  
- `edi-278`: 30 days
- `dead-letter-queue`: 90 days

### S3 Data Lake Backup

```bash
# Sync to backup bucket
aws s3 sync s3://hipaa-attachments s3://hipaa-attachments-backup \
  --sse AES256 --storage-class STANDARD_IA
```

## Disaster Recovery

### Full Cluster Recovery

1. **Restore Kubernetes cluster**

2. **Restore Kafka data** (if using persistent volumes)

3. **Redeploy application stack**:
   ```bash
   helm install cloudhealthoffice ./helm/cloudhealthoffice -n cloudhealthoffice
   ```

4. **Restore secrets** from backup vault

5. **Verify connectivity**:
   ```bash
   kubectl apply -f tests/integration/connectivity-test.yaml
   ```

6. **Replay missed messages** from Kafka offsets or S3 archive

### Single Workflow Recovery

```bash
# Resubmit failed workflow
argo resubmit <workflow-name> -n cloudhealthoffice

# Submit new workflow with specific file
argo submit argo-workflows/x12-275-ingest.yaml \
  -n cloudhealthoffice \
  -p sftp-host=sftp.clearinghouse.example.com \
  -p sftp-folder=/inbound/attachments
```

## Performance Tuning

### Workflow Parallelism

```yaml
# In WorkflowTemplate spec
spec:
  parallelism: 10  # Max concurrent pods
  podGC:
    strategy: OnWorkflowSuccess
```

### Kafka Consumer Tuning

```yaml
# In Argo Events Kafka EventSource
spec:
  kafka:
    rfai-requests:
      consumerGroup:
        groupName: "argo-rfai-processor"
        rebalanceStrategy: "sticky"
      config:
        max.poll.records: "500"
        fetch.min.bytes: "1024"
```

### Resource Optimization

```yaml
# Container resource limits
resources:
  requests:
    memory: "256Mi"
    cpu: "100m"
  limits:
    memory: "512Mi"
    cpu: "500m"
```

## Security Operations

### Secret Rotation

```bash
# Rotate SFTP password
kubectl create secret generic clearinghouse-sftp-secret \
  --from-literal=username=$SFTP_USERNAME \
  --from-literal=password=$NEW_PASSWORD \
  -n cloudhealthoffice --dry-run=client -o yaml | kubectl apply -f -

# Rotate Kafka credentials
kubectl create secret generic kafka-sasl-secret \
  --from-literal=mechanism=SCRAM-SHA-512 \
  --from-literal=username=$KAFKA_USERNAME \
  --from-literal=password=$NEW_PASSWORD \
  -n cloudhealthoffice --dry-run=client -o yaml | kubectl apply -f -
```

### Audit Logging

Workflow execution is logged to:
- Argo Workflows archive
- Kafka topics (for event tracing)
- Container stdout (forwarded to logging backend)

Query audit logs:
```bash
# Get all workflow events for a claim
kubectl logs -l workflows.argoproj.io/workflow-template=x12-275-ingest \
  -n cloudhealthoffice | grep "CLM20250924001"
```

## Maintenance Windows

### Planned Maintenance

1. **Pause event sources**:
   ```bash
   kubectl patch eventsource sftp-polling -n cloudhealthoffice \
     --type=merge -p '{"spec":{"calendar":{"sftp-poll-275":{"schedule":""}}}}'
   ```

2. **Wait for running workflows to complete**:
   ```bash
   kubectl wait --for=condition=Completed workflows -l app=cloudhealthoffice \
     -n cloudhealthoffice --timeout=30m
   ```

3. **Perform maintenance**

4. **Resume event sources**:
   ```bash
   kubectl apply -f argo-events/sftp-event-source.yaml
   ```

### Rolling Updates

```bash
# Update container images with zero downtime
kubectl set image deployment/argo-workflows-controller \
  controller=cloudhealthoffice/x12-parser:v1.1.0 \
  -n cloudhealthoffice
```

## Support Contacts

- **On-Call Rotation**: Check PagerDuty
- **Slack Channel**: #cloudhealthoffice-ops
- **Escalation**: edi-platform@company.com
- **Documentation**: https://docs.cloudhealthoffice.com
