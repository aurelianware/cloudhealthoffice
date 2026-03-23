# Argo Workflows Migration Guide

> **Status: COMPLETE.** The migration from Azure Logic Apps to Argo Workflows is finished. All EDI orchestration now runs on Argo Workflows on AKS. See [ADR-004](../adr/004-remove-logic-apps.md) for the decision record. This document is retained as a reference for the migration process that was followed.

This guide documents the migration from Azure Logic Apps to Argo Workflows for X12 EDI processing in Cloud Health Office.

## Overview

The migration moved X12 EDI processing from Azure-native services to a cloud-agnostic Kubernetes architecture:

| Component | Azure (Legacy) | Kubernetes (Current) |
|-----------|-----------------|---------------------|
| Workflow Orchestration | Logic Apps Standard | Argo Workflows |
| Event Streaming | Service Bus Topics | Apache Kafka |
| File Transfer | Logic Apps SFTP Connector | Custom SFTP Container |
| X12 Processing | Integration Account | pyx12 Python Library |
| Storage | Data Lake Gen2 | S3-compatible (MinIO/AWS) |
| Secrets Management | Key Vault | Kubernetes Secrets |

## Prerequisites

### Infrastructure Requirements

1. **Kubernetes Cluster** (1.27+)
   - AKS, EKS, GKE, or self-managed
   - Minimum 3 worker nodes
   - 8 GB RAM, 4 vCPU per node

2. **Helm 3.x** for chart deployment

3. **kubectl** configured for cluster access

4. **Container Registry** (Docker Hub, ECR, ACR, or Harbor)

### Access Requirements

- Clearinghouse SFTP credentials
- claims backend API token
- S3/MinIO credentials
- Kafka SASL credentials (if using external Kafka)

## Migration Steps

### Phase 1: Parallel Infrastructure Setup

Deploy new infrastructure alongside existing Logic Apps.

```bash
# Create namespace
kubectl create namespace cloudhealthoffice

# Install Argo Workflows
helm repo add argo https://argoproj.github.io/argo-helm
helm install argo-workflows argo/argo-workflows -n cloudhealthoffice \
  --set controller.parallelism=10 \
  --set server.enabled=true

# Install Argo Events
helm install argo-events argo/argo-events -n cloudhealthoffice

# Install Kafka (or configure external)
helm repo add bitnami https://charts.bitnami.com/bitnami
helm install kafka bitnami/kafka -n kafka -f kafka/values.yaml

# Create Kafka topics
kubectl apply -f kafka/topics.yaml
```

### Phase 2: Build and Push Container Images

```bash
# Build all containers
cd containers/x12-parser
docker build -t cloudhealthoffice/x12-parser:latest .

cd ../x12-encoder
docker build -t cloudhealthoffice/x12-encoder:latest .

cd ../sftp-fetcher
docker build -t cloudhealthoffice/sftp-fetcher:latest .

cd ../metadata-extractor
docker build -t cloudhealthoffice/metadata-extractor:latest .

cd ../kafka-publisher
docker build -t cloudhealthoffice/kafka-publisher:latest .

# Push to registry
docker push cloudhealthoffice/x12-parser:latest
docker push cloudhealthoffice/x12-encoder:latest
docker push cloudhealthoffice/sftp-fetcher:latest
docker push cloudhealthoffice/metadata-extractor:latest
docker push cloudhealthoffice/kafka-publisher:latest
```

### Phase 3: Deploy Secrets and ConfigMaps

```bash
# Create secrets (replace placeholder values)
kubectl create secret generic clearinghouse-sftp-secret \
  --from-literal=username=$SFTP_USERNAME \
  --from-literal=password=$SFTP_PASSWORD \
  -n cloudhealthoffice

kubectl create secret generic kafka-sasl-secret \
  --from-literal=mechanism=SCRAM-SHA-512 \
  --from-literal=username=$KAFKA_USERNAME \
  --from-literal=password=$KAFKA_PASSWORD \
  -n cloudhealthoffice

kubectl create secret generic claims-backend-api-secret \
  --from-literal=token=$CLAIMS_BACKEND_API_TOKEN \
  -n cloudhealthoffice

kubectl create secret generic s3-credentials-secret \
  --from-literal=access-key-id=$AWS_ACCESS_KEY_ID \
  --from-literal=secret-access-key=$AWS_SECRET_ACCESS_KEY \
  -n cloudhealthoffice

# Apply ConfigMaps
kubectl apply -f k8s/configmaps/
```

### Phase 4: Deploy Argo Workflows

```bash
# Deploy RBAC
kubectl apply -f k8s/rbac/argo-rbac.yaml

# Deploy WorkflowTemplates
kubectl apply -f argo-workflows/x12-275-ingest.yaml
kubectl apply -f argo-workflows/x12-278-ingest.yaml
kubectl apply -f argo-workflows/x12-277-rfai.yaml
kubectl apply -f argo-workflows/x12-278-replay.yaml

# Deploy Argo Events
kubectl apply -f argo-events/sftp-event-source.yaml
kubectl apply -f argo-events/kafka-event-source.yaml
kubectl apply -f argo-events/sensors/
```

### Phase 5: Parallel Run Testing

Run both systems in parallel to verify output parity.

```bash
# Start parallel run script
./migration/parallel-run.sh --percentage 10

# Monitor both systems
watch kubectl get workflows -n cloudhealthoffice
```

### Phase 6: Traffic Migration

Gradually increase traffic to Argo Workflows:

1. **10% traffic** - Initial validation (1 week)
2. **25% traffic** - Stability confirmation (1 week)
3. **50% traffic** - Performance validation (1 week)
4. **100% traffic** - Full migration

### Phase 7: Decommission Logic Apps

After successful migration:

1. Stop Logic App triggers
2. Export final workflow run history
3. Archive Integration Account schemas
4. Delete Logic Apps resources

## Workflow Mapping

### ingest275 → x12-275-ingest

| Logic App Action | Argo Workflow Step |
|------------------|-------------------|
| SFTP_New_or_Updated_File | sftp-fetch (triggered by sensor) |
| Get_file_content | sftp-fetch container |
| Store_Raw_in_Blob | store-data-lake step |
| Decode_X12_275 | parse-x12-275 step |
| Extract_Metadata | extract-metadata step |
| Send_to_ServiceBus | publish-kafka step |
| Call_CLAIMS_BACKEND_API | (configurable in metadata-extractor) |
| Delete_SFTP_File | cleanup-sftp step |

### ingest278 → x12-278-ingest

| Logic App Action | Argo Workflow Step |
|------------------|-------------------|
| ServiceBus_Edi278_Messages | Kafka sensor trigger |
| Get_Blob_Content | sftp-fetch step |
| Store_Raw_in_Blob_278 | store-data-lake step |
| Decode_X12_278 | parse-x12-278 step |
| Extract_278_Metadata | extract-auth-data step |
| Call_Claims_Backend_278_API | call-claims-backend-api step |
| Log_Custom_Event | publish-kafka step |

### rfai277 → x12-277-rfai

| Logic App Action | Argo Workflow Step |
|------------------|-------------------|
| ServiceBus_RFAI_Requests | Kafka sensor trigger (rfai-requests topic) |
| Parse_RFAI_Payload | parse-rfai-request step |
| Compose_277_JSON | (in generate-277 step) |
| Encode_X12_277 | generate-277 step |
| Drop_to_SFTP_Outbound | sftp-upload step |

### replay278 → x12-278-replay

| Logic App Action | Argo Workflow Step |
|------------------|-------------------|
| HTTP_Replay_278_Request | HTTP workflow trigger or Kafka message |
| Validate_Blob_Url | check-idempotency step |
| Send_to_Edi278_Topic | record-replay step (Kafka publish) |

## Configuration Changes

### Trading Partner Configuration

Logic Apps parameters → Kubernetes ConfigMap:

```yaml
# Logic App parameter
"x12_sender_id": { "type": "String", "defaultValue": "030240928" }

# Kubernetes ConfigMap
apiVersion: v1
kind: ConfigMap
metadata:
  name: trading-partners-config
data:
  clearinghouse-id: "030240928"
```

### Secret Migration

Azure Key Vault → Kubernetes Secrets:

```bash
# Export from Key Vault
az keyvault secret show --name claims-backend-api-token --vault-name myvault --query value -o tsv

# Create Kubernetes Secret
kubectl create secret generic claims-backend-api-secret \
  --from-literal=token=<exported-value> \
  -n cloudhealthoffice
```

## Monitoring Migration

### Azure Monitor → Prometheus/Grafana

| Azure Monitor | Prometheus |
|--------------|------------|
| Logic App run metrics | argo_workflows_count |
| Service Bus metrics | kafka_consumergroup_lag |
| Custom events | Custom metrics via pushgateway |

Deploy monitoring:

```bash
kubectl apply -f monitoring/prometheus-rules.yaml

# Import Grafana dashboard
kubectl create configmap grafana-dashboards \
  --from-file=monitoring/grafana/ \
  -n monitoring
```

## Rollback Procedure

If issues arise during migration:

1. **Stop Argo Event Sources**
   ```bash
   kubectl delete eventsource sftp-polling -n cloudhealthoffice
   kubectl delete eventsource kafka-events -n cloudhealthoffice
   ```

2. **Re-enable Logic App Triggers**
   ```bash
   az logicapp trigger enable --name ingest275 --resource-group myRG --trigger-name SFTP_New_or_Updated_File
   ```

3. **Route Kafka messages back to Service Bus**
   ```bash
   ./migration/parallel-run.sh --route-to-azure
   ```

## Troubleshooting

### Common Issues

#### Workflow Stuck in Pending State

```bash
# Check events
kubectl describe workflow <workflow-name> -n cloudhealthoffice

# Check pod status
kubectl get pods -n cloudhealthoffice -l workflows.argoproj.io/workflow=<workflow-name>
```

#### SFTP Connection Failures

```bash
# Test SFTP connectivity
kubectl run sftp-test --rm -it --image=cloudhealthoffice/sftp-fetcher \
  --env="SFTP_HOST=sftp.clearinghouse.example.com" \
  --env="SFTP_USERNAME=$USER" \
  -- --list-only
```

#### Kafka Consumer Lag

```bash
# Check consumer lag
kubectl exec -it kafka-0 -n kafka -- \
  kafka-consumer-groups.sh --bootstrap-server localhost:9092 \
  --describe --group argo-rfai-processor
```

## Performance Comparison

Target metrics to validate migration success:

| Metric | Logic Apps | Argo Workflows (Target) |
|--------|-----------|------------------------|
| 275 Processing Time | ~2 min | <2 min |
| 278 Processing Time | ~1 min | <1 min |
| 277 Generation Time | ~30 sec | <30 sec |
| Daily Throughput | 10,000 tx | 10,000+ tx |
| Error Rate | <1% | <1% |

## Support

For migration assistance:
- GitHub Issues: https://github.com/aurelianware/cloudhealthoffice/issues
- Documentation: https://docs.cloudhealthoffice.com
