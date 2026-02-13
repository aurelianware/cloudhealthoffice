# 837 Claims Ingestion End-to-End Pipeline

Complete pipeline for importing 837 EDI files, creating claims, and triggering adjudication.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                   837 Claims Import Pipeline                     │
└─────────────────────────────────────────────────────────────────┘

SFTP Server (/inbound/claims/*.edi)
        │
        ↓
┌───────────────────────────────────────┐
│   Argo Workflow: x12-837-ingest      │
│   (Runs every 5 minutes via cron)    │
│                                       │
│   1. Fetch from SFTP                  │
│   2. Parse 837 EDI → JSON             │
│   3. POST to Claims Service API       │
│   4. Publish to Kafka topic           │
│   5. Archive to SFTP                  │
└───────────────┬───────────────────────┘
                │
                ↓
        Kafka Topic: claims-adjudication
                │
                ↓
┌───────────────────────────────────────┐
│   Argo Events Sensor                  │
│   (Kafka consumer)                    │
└───────────┬───────────────────────────┘
            │
            ↓
┌───────────────────────────────────────┐
│   Argo Workflow:                      │
│   claims-adjudication-template        │
│   (10-step DAG)                       │
│                                       │
│   1. Get claim                        │
│   2. Validate codes                   │
│   3. Verify coverage                  │
│   4. Validate provider                │
│   5. Check prior auth                 │
│   6. Get benefits                     │
│   7. Get rates                        │
│   8. Calculate allowed                │
│   9. Calculate cost-sharing           │
│   10. Update claim (APPROVED/DENIED)  │
└───────────────────────────────────────┘
```

## Components Created

### 1. Kafka Topics (`kafka/topics.yaml`)
- **claims-adjudication** - 6 partitions, 30-day retention
- **claims-work-queue** - 3 partitions, flagged claims
- **claims-rejected** - 3 partitions, 90-day retention

### 2. Argo Workflow: x12-837-ingest (`argo-workflows/x12-837-ingest.yaml`)
**Purpose:** Ingest 837 files from SFTP and create claims

**Steps:**
1. **fetch-from-sftp** - Download 837 files
2. **parse-837-files** - Parse EDI to JSON
3. **create-claims-batch** - POST to Claims Service + Kafka publish
4. **archive-to-sftp** - Move processed files to archive

**Execution:**
- CronWorkflow runs every 5 minutes
- Manual: `argo submit -n cloudhealthoffice --from workflowtemplate/x12-837-ingest`

### 3. Argo Events EventSource (`argo-events/claims-adjudication-eventsource.yaml`)
**Purpose:** Listen to Kafka topic and trigger adjudication

**Configuration:**
- Kafka topic: `claims-adjudication`
- Consumer group: `argo-claims-adjudication`
- SASL/SSL authentication
- Triggers: `claims-adjudication-template` workflow

### 4. Container Images

**x12-parser** (`containers/x12-parser/`)
- Base: `node:18-alpine`
- Parse 837P/837I/837D EDI to JSON
- Uses `@hahntech/x12-parser` npm package

**claims-publisher** (`containers/claims-publisher/`)
- Base: `alpine:3.18`
- POST claims to Claims Service API
- Publish to Kafka using `kafkacat`

## Deployment

### 1. Create Kafka Topics
```bash
kubectl apply -f kafka/topics.yaml
```

### 2. Build Container Images
```bash
# x12-parser
cd containers/x12-parser
docker build -t cloudhealthoffice/x12-parser:latest .
docker push cloudhealthoffice/x12-parser:latest

# claims-publisher
cd ../claims-publisher
docker build -t cloudhealthoffice/claims-publisher:latest .
docker push cloudhealthoffice/claims-publisher:latest
```

### 3. Deploy Argo Workflow
```bash
kubectl apply -f argo-workflows/x12-837-ingest.yaml
```

### 4. Deploy Argo Events
```bash
kubectl apply -f argo-events/claims-adjudication-eventsource.yaml
```

## Testing End-to-End

### 1. Generate Test 837 File
```bash
cd scripts/utils
npm run generate-837 > test-claim-001.edi
```

### 2. Upload to SFTP
```bash
sftp sftp-user@sftp-server.cloudhealthoffice.svc.cluster.local
cd /inbound/claims
put test-claim-001.edi
bye
```

### 3. Trigger Workflow (or wait for cron)
```bash
argo submit -n cloudhealthoffice --from workflowtemplate/x12-837-ingest
```

### 4. Monitor Workflow
```bash
# Watch workflow execution
argo watch -n cloudhealthoffice @latest

# Check logs
argo logs -n cloudhealthoffice @latest

# Verify claim created
curl http://claims-service.cloudhealthoffice/api/claims?status=Submitted
```

### 5. Verify Adjudication Triggered
```bash
# Check Kafka messages
kubectl exec -it cloudhealthoffice-kafka-0 -n kafka -- \
  kafka-console-consumer.sh \
  --bootstrap-server localhost:9092 \
  --topic claims-adjudication \
  --from-beginning

# Watch adjudication workflow
argo list -n cloudhealthoffice | grep claims-adjudication
```

### 6. Check Final Claim Status
```bash
# Should be APPROVED or DENIED after adjudication
curl http://claims-service.cloudhealthoffice/api/claims/{claim-id}
```

## Flow Summary

1. **Drop 837 file** → SFTP `/inbound/claims/`
2. **Cron triggers** → `x12-837-ingest` workflow (every 5 min)
3. **Parse EDI** → JSON claim structure
4. **Create claim** → POST to Claims Service API
5. **Publish event** → Kafka `claims-adjudication` topic
6. **Argo Events** → Detects message, triggers adjudication
7. **Adjudication** → 10-step workflow executes
8. **Claim updated** → Status = APPROVED/DENIED

## Configuration

### SFTP Settings
Edit `x12-837-ingest.yaml`:
```yaml
arguments:
  parameters:
    - name: sftp-host
      value: "your-sftp-host"
    - name: sftp-folder
      value: "/inbound/claims"
```

### Kafka Settings
Edit `claims-adjudication-eventsource.yaml`:
```yaml
kafka:
  claims-adjudication:
    url: cloudhealthoffice-kafka-bootstrap.kafka:9092
    topic: claims-adjudication
```

## Monitoring

### Workflow Metrics
```bash
# View workflow history
argo list -n cloudhealthoffice

# Check failed workflows
argo list -n cloudhealthoffice --status Failed

# Get workflow details
argo get -n cloudhealthoffice <workflow-name>
```

### Kafka Consumer Lag
```bash
kubectl exec -it cloudhealthoffice-kafka-0 -n kafka -- \
  kafka-consumer-groups.sh \
  --bootstrap-server localhost:9092 \
  --describe \
  --group argo-claims-adjudication
```

### Claims Service Health
```bash
curl http://claims-service.cloudhealthoffice/health
```

## Troubleshooting

### No files processed
- Check SFTP folder: `sftp sftp-user@sftp-server`
- Verify file pattern: `*.edi`
- Check workflow logs: `argo logs -n cloudhealthoffice @latest`

### Parse errors
- Check x12-parser logs
- Verify 837 format (005010X222A1/X223A2/X224A2)
- Test with `scripts/utils/generate-837-claims.ts`

### Claims not created
- Check Claims Service API: `curl http://claims-service.cloudhealthoffice/health`
- Verify tenant ID matches
- Check HTTP response codes in workflow logs

### Adjudication not triggered
- Check Kafka topic: `kafka-console-consumer.sh`
- Verify EventSource pod running: `kubectl get pods -n cloudhealthoffice`
- Check Sensor logs: `kubectl logs -n cloudhealthoffice -l sensor-name=claims-adjudication-trigger`

## Next Steps

1. **Configure actual x12-parser** - Replace stub with real 837 parser
2. **Add validation** - Integrate with claims-scrubbing-service
3. **Error handling** - Route invalid claims to work queue
4. **Monitoring** - Add Prometheus metrics
5. **Alerting** - Configure alerts for failed workflows
6. **Testing** - E2E test with real 837 files
