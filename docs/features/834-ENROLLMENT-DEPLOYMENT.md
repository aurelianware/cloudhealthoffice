# 834 Enrollment Import Pipeline - Deployment Guide

## Overview

The 834 Enrollment Import Pipeline processes X12 834 Benefit Enrollment and Maintenance transactions from employers and health plans. It automates member enrollment lifecycle (additions, changes, terminations), populating Cosmos DB with real member, coverage, and sponsor data.

**Pipeline Components:**
- **X12 834 Parser Container** (Node.js): Parses .edi files to JSON
- **Enrollment Import Service** (.NET 8): Processes JSON, writes to Cosmos DB
- **Argo CronWorkflow**: Automated SFTP → Parse → Import → Archive pipeline

**Data Flow:**
```
Employer SFTP → fetch-from-sftp → parse-834-files → import-to-cosmos → archive-to-sftp
```

---

## Prerequisites

### 1. Cosmos DB Setup

Create database and containers:

```bash
# Set variables
COSMOS_ACCOUNT="<your-cosmos-account-name>"
RESOURCE_GROUP="<your-resource-group>"
DATABASE_NAME="CloudHealthOffice"

# Create database (if not exists)
az cosmosdb sql database create \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --name $DATABASE_NAME \
  --throughput 1000

# Create Members container
az cosmosdb sql container create \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --database-name $DATABASE_NAME \
  --name Members \
  --partition-key-path "/id" \
  --throughput 400

# Create Coverage container
az cosmosdb sql container create \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --database-name $DATABASE_NAME \
  --name Coverage \
  --partition-key-path "/id" \
  --throughput 400

# Create Sponsors container
az cosmosdb sql container create \
  --account-name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --database-name $DATABASE_NAME \
  --name Sponsors \
  --partition-key-path "/id" \
  --throughput 400

# Get Cosmos DB endpoint and key
COSMOS_ENDPOINT=$(az cosmosdb show \
  --name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --query documentEndpoint -o tsv)

COSMOS_KEY=$(az cosmosdb keys list \
  --name $COSMOS_ACCOUNT \
  --resource-group $RESOURCE_GROUP \
  --query primaryMasterKey -o tsv)

echo "Cosmos DB Endpoint: $COSMOS_ENDPOINT"
echo "Cosmos DB Key: $COSMOS_KEY"
```

### 2. Kubernetes Secrets

Create Cosmos DB secret:

```bash
kubectl create secret generic database-secret \
  --namespace cloudhealthoffice \
  --from-literal=endpoint="$COSMOS_ENDPOINT" \
  --from-literal=key="$COSMOS_KEY"
```

Create SFTP credentials secret:

```bash
kubectl create secret generic sftp-creds \
  --namespace cloudhealthoffice \
  --from-literal=username="<sftp-username>" \
  --from-literal=password="<sftp-password>"
```

### 3. Kafka Topics

Deploy enrollment-import topic:

```bash
kubectl apply -f kafka/topics.yaml
```

Verify topic created:

```bash
kubectl get kafkatopics -n kafka enrollment-import
```

---

## Deployment Steps

### Step 1: Build Docker Images

Build and push containers to GHCR:

```bash
# X12 834 Parser
docker build -t ghcr.io/aurelianware/cloudhealthoffice-x12-834-parser:latest \
  containers/x12-834-parser

docker push ghcr.io/aurelianware/cloudhealthoffice-x12-834-parser:latest

# Enrollment Import Service
docker build -t ghcr.io/aurelianware/cloudhealthoffice-enrollment-import-service:latest \
  services/enrollment-import-service

docker push ghcr.io/aurelianware/cloudhealthoffice-enrollment-import-service:latest
```

**Note:** If using GitHub Actions, these images are built automatically on push to `main`.

### Step 2: Deploy Enrollment Import Service

Deploy microservice to Kubernetes:

```bash
kubectl apply -f services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml
```

Verify deployment:

```bash
# Check pods
kubectl get pods -n cloudhealthoffice -l app=enrollment-import-service

# Check service
kubectl get svc -n cloudhealthoffice enrollment-import-service

# Check HPA
kubectl get hpa -n cloudhealthoffice enrollment-import-service-hpa

# View logs
kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=50 -f
```

Expected output:
```
NAME                                        READY   STATUS    RESTARTS   AGE
enrollment-import-service-xxxxxxxxx-xxxxx   1/1     Running   0          2m
enrollment-import-service-xxxxxxxxx-xxxxx   1/1     Running   0          2m
```

### Step 3: Deploy Argo Workflow

Deploy 834 CronWorkflow:

```bash
kubectl apply -f argo-workflows/x12-834-enrollment-import.yaml
```

Verify CronWorkflow:

```bash
# List CronWorkflows
kubectl get cronworkflows -n cloudhealthoffice

# Describe CronWorkflow
kubectl describe cronworkflow x12-834-enrollment-import -n cloudhealthoffice

# View workflow template
argo cron get x12-834-enrollment-import -n cloudhealthoffice
```

### Step 4: Configure SFTP Server

Ensure SFTP server has required directories:

```bash
# Connect to SFTP server
sftp <username>@<sftp-host>

# Create directories
mkdir -p /inbound/enrollment
mkdir -p /archive/834

# Set permissions (read/write for automation user)
chmod 770 /inbound/enrollment
chmod 770 /archive/834

# Verify directories
ls -la /inbound/
ls -la /archive/

# Exit SFTP
exit
```

---

## Testing the Pipeline

### Test 1: Manual Workflow Trigger

Trigger workflow manually without waiting for cron schedule:

```bash
# Submit workflow from CronWorkflow template
argo submit --from cronwf/x12-834-enrollment-import -n cloudhealthoffice \
  --parameter sftp-host="<sftp-host>" \
  --parameter sftp-path="/inbound/enrollment" \
  --parameter tenant-id="<tenant-id>"

# Watch workflow execution
argo watch @latest -n cloudhealthoffice

# View workflow logs
argo logs @latest -n cloudhealthoffice
```

### Test 2: Upload Sample 834 File

Upload test file to SFTP:

```bash
# Upload sample file
sftp <username>@<sftp-host>
put test-x12-834-enrollment-sample.edi /inbound/enrollment/test-enrollment-20260201.edi
exit

# Wait for CronWorkflow (runs every 10 minutes) or trigger manually
argo submit --from cronwf/x12-834-enrollment-import -n cloudhealthoffice

# View logs
argo logs @latest -n cloudhealthoffice
```

### Test 3: Verify Data in Cosmos DB

Query Cosmos DB to verify imported data:

```bash
# Azure Portal:
# 1. Navigate to Cosmos DB account
# 2. Open Data Explorer
# 3. Select CloudHealthOffice database
# 4. Query Members container:

SELECT * FROM c WHERE c.tenantId = "<tenant-id>"

# Expected: 6 members (3 subscribers + 3 dependents)
# - John Smith (subscriber)
# - Jane Smith (spouse)
# - Michael Smith (child)
# - Sarah Johnson (subscriber)
# - Robert Johnson (spouse)
# - Robert Williams (subscriber, terminated)

# Query Coverage container:
SELECT * FROM c WHERE c.tenantId = "<tenant-id>"

# Expected: 6+ coverage records (health, dental, vision for members)

# Query Sponsors container:
SELECT * FROM c WHERE c.tenantId = "<tenant-id>"

# Expected: 1 sponsor (Acme Corporation)
```

### Test 4: Validate Import Results

Check import statistics in workflow output:

```bash
argo logs @latest -n cloudhealthoffice | grep "totalEnrollments"
```

Expected output:
```json
{
  "totalFiles": 1,
  "totalEnrollments": 3,
  "membersCreated": 6,
  "membersUpdated": 0,
  "membersTerminated": 1,
  "dependentsCreated": 3,
  "coverageRecordsCreated": 6
}
```

### Test 5: Check REST API Directly

Test enrollment-import-service API endpoint:

```bash
# Port-forward service
kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8080:80

# In another terminal, send parsed 834 JSON
curl -X POST http://localhost:8080/api/v1/enrollment/import \
  -H "Content-Type: application/json" \
  -H "X-Tenant-ID: <tenant-id>" \
  -d @<parsed-834-json-file>

# Expected response:
{
  "successCount": 3,
  "failedCount": 0,
  "skippedCount": 0,
  "membersCreated": 6,
  "membersUpdated": 0,
  "membersTerminated": 1,
  "dependentsCreated": 3,
  "coverageRecordsCreated": 6,
  "errors": []
}
```

---

## Monitoring

### Health Checks

```bash
# Check enrollment-import-service health
kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8080:80
curl http://localhost:8080/health

# Expected: HTTP 200 OK
```

### View Recent Workflows

```bash
# List recent workflow executions
argo list -n cloudhealthoffice --prefix x12-834-enrollment-import

# View specific workflow
argo get <workflow-name> -n cloudhealthoffice

# View workflow logs
argo logs <workflow-name> -n cloudhealthoffice
```

### Monitor Kafka Topic

```bash
# View enrollment-import topic messages
kubectl exec -it -n kafka cloudhealthoffice-kafka-0 -- \
  /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server localhost:9092 \
  --topic enrollment-import \
  --from-beginning \
  --max-messages 10
```

### Prometheus Metrics

Enrollment-import-service exposes Prometheus metrics at `/metrics`:

```bash
kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8080:80
curl http://localhost:8080/metrics
```

Key metrics:
- `http_requests_total`: Total HTTP requests
- `http_request_duration_seconds`: Request latency
- `enrollment_imports_total`: Total enrollment imports
- `enrollment_members_created_total`: Total members created
- `enrollment_members_terminated_total`: Total terminations

---

## Troubleshooting

### Issue: Workflow fails at fetch-from-sftp step

**Symptoms:**
```
Error: Failed to connect to SFTP server
```

**Solution:**
1. Verify SFTP credentials secret:
```bash
kubectl get secret sftp-creds -n cloudhealthoffice -o yaml
```

2. Check SFTP host reachability:
```bash
kubectl run -it --rm debug --image=alpine --restart=Never -- \
  nc -zv <sftp-host> 22
```

3. Test SFTP login manually:
```bash
sftp <username>@<sftp-host>
```

### Issue: Workflow fails at parse-834-files step

**Symptoms:**
```
Error: Invalid X12 transaction
```

**Solution:**
1. View parser logs:
```bash
argo logs <workflow-name> -n cloudhealthoffice --container parse-834-files
```

2. Check .error.json output for parse failures
3. Validate 834 file structure:
   - Must have ISA/GS/ST/BGN/INS segments
   - Must have IEA/GE/SE trailer segments
   - Segment terminators must be consistent (~)

### Issue: Workflow fails at import-to-cosmos step

**Symptoms:**
```
Error: 401 Unauthorized (Cosmos DB)
```

**Solution:**
1. Verify Cosmos DB secret:
```bash
kubectl get secret database-secret -n cloudhealthoffice -o yaml
```

2. Check enrollment-import-service logs:
```bash
kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=100
```

3. Verify Cosmos DB endpoint/key are correct:
```bash
az cosmosdb keys list --name <cosmos-account> --resource-group <resource-group>
```

### Issue: Members not created in Cosmos DB

**Symptoms:**
- Workflow succeeds but Cosmos DB queries return empty results

**Solution:**
1. Check X-Tenant-ID header in workflow parameters
2. Verify partition key (/id) matches entity id field
3. Query without partition key filter:
```sql
SELECT * FROM c
```

4. Check import statistics in workflow output:
```bash
argo logs @latest -n cloudhealthoffice | grep "membersCreated"
```

### Issue: HPA not scaling pods

**Symptoms:**
- High CPU/memory but replicas not increasing

**Solution:**
1. Check HPA status:
```bash
kubectl describe hpa enrollment-import-service-hpa -n cloudhealthoffice
```

2. Verify metrics-server is running:
```bash
kubectl get deployment metrics-server -n kube-system
```

3. Check resource metrics:
```bash
kubectl top pods -n cloudhealthoffice -l app=enrollment-import-service
```

---

## Configuration

### Adjust CronWorkflow Schedule

Edit `argo-workflows/x12-834-enrollment-import.yaml`:

```yaml
spec:
  schedule: "*/10 * * * *"  # Every 10 minutes (default)
  # Examples:
  # "0 * * * *"     # Hourly
  # "0 0 * * *"     # Daily at midnight
  # "0 */6 * * *"   # Every 6 hours
```

Apply changes:
```bash
kubectl apply -f argo-workflows/x12-834-enrollment-import.yaml
```

### Adjust HPA Scaling Thresholds

Edit `services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml`:

```yaml
spec:
  minReplicas: 2       # Minimum pods (default)
  maxReplicas: 10      # Maximum pods (default)
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 70  # Scale at 70% CPU (default)
```

Apply changes:
```bash
kubectl apply -f services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml
```

### Adjust Resource Limits

Edit `services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml`:

```yaml
resources:
  requests:
    cpu: 250m      # Minimum guaranteed CPU
    memory: 384Mi  # Minimum guaranteed memory
  limits:
    cpu: 500m      # Maximum CPU
    memory: 512Mi  # Maximum memory
```

Apply changes:
```bash
kubectl apply -f services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml
```

---

## Next Steps

### 1. Update Existing Services to Use Real Data

Modify existing microservices to read from Cosmos DB instead of mock data:

- **Member Service**: Query Members container
- **Coverage Service**: Query Coverage container
- **Sponsor Service**: Query Sponsors container
- **Claims Service**: Reference real members during adjudication
- **Eligibility Service**: Check real coverage records

### 2. Implement 837 Claims Integration

Link claims processing to real member enrollments:

```csharp
// In claims-service, validate member exists before adjudication
var member = await _memberRepository.GetMemberByIdAsync(tenantId, memberId);
if (member == null || member.Status != "Active")
{
    return new ClaimResult { Status = "Rejected", Reason = "Member not found or inactive" };
}

// Validate coverage
var coverage = await _coverageRepository.GetActiveCoverageAsync(tenantId, memberId, serviceDate);
if (coverage == null)
{
    return new ClaimResult { Status = "Rejected", Reason = "No active coverage on service date" };
}
```

### 3. Implement COBRA/Termination Workflows

Automate member termination handling:

- Trigger eligibility checks for terminated members
- Send notifications to member portal
- Update claims adjudication rules (grace periods, runout periods)

### 4. Implement Dependent Management

Build workflows for dependent lifecycle:

- Automatic dependent aging (child → adult transition)
- Dependent verification requests (annual recertification)
- Court-ordered dependent coverage enforcement

### 5. Add Event Publishing

Publish enrollment events to Kafka for downstream processing:

```csharp
// In EnrollmentImportService.cs
await _kafkaProducer.ProduceAsync("enrollment-import", new Message<string, EnrollmentEvent>
{
    Key = memberId,
    Value = new EnrollmentEvent
    {
        TenantId = tenantId,
        MemberId = memberId,
        EventType = "MemberEnrolled",
        Timestamp = DateTime.UtcNow,
        Data = member
    }
});
```

---

## Appendix

### Sample 834 File Structure

The `test-x12-834-enrollment-sample.edi` file demonstrates:

**Member 1: John Smith (Employee, Active, Family Coverage)**
- SubscriberId: BSCA123456789
- Coverage: Health (PPO) + Dental (Basic) + Vision (Standard)
- Dependents: Jane Smith (spouse), Michael Smith (child)
- MaintenanceType: 021 (Addition)

**Member 2: Sarah Johnson (Employee, Active, Employee+Spouse)**
- SubscriberId: BSCA987654321
- Coverage: Health (HMO)
- Dependent: Robert Johnson (spouse)
- MaintenanceType: 021 (Addition)

**Member 3: Robert Williams (Employee, Terminated)**
- SubscriberId: BSCA555666777
- Enrollment: 2025-01-15, Termination: 2026-01-31
- MaintenanceType: 001 (Change), BenefitStatus: T (Terminated)

### Cosmos DB Schema

**Members Container:**
```json
{
  "id": "MSMI850315A3F7",
  "tenantId": "tenant-123",
  "memberId": "MSMI850315A3F7",
  "subscriberId": "BSCA123456789",
  "firstName": "John",
  "lastName": "Smith",
  "dateOfBirth": "1985-03-15",
  "gender": "M",
  "ssn": "123-45-6789",
  "address": "123 Main St, Anytown, CA 12345",
  "status": "Active",
  "enrollmentDate": "2026-02-01",
  "terminationDate": null,
  "sponsorId": "123456789",
  "groupNumber": "GRP0001",
  "dependentIds": ["JSMI870520B8C9", "MSMI150610D4E5"],
  "createdAt": "2026-02-01T10:00:00Z",
  "updatedAt": "2026-02-01T10:00:00Z"
}
```

**Coverage Container:**
```json
{
  "id": "COV-MSMI850315A3F7-HLT-001",
  "tenantId": "tenant-123",
  "memberId": "MSMI850315A3F7",
  "planId": "PPO-2026",
  "insuranceType": "HLT",
  "coverageLevel": "EMP",
  "effectiveDate": "2026-02-01",
  "terminationDate": null,
  "createdAt": "2026-02-01T10:00:00Z",
  "updatedAt": "2026-02-01T10:00:00Z"
}
```

**Sponsors Container:**
```json
{
  "id": "123456789",
  "tenantId": "tenant-123",
  "sponsorId": "123456789",
  "name": "Acme Corporation",
  "federalTaxId": "12-3456789",
  "groupNumber": "GRP0001",
  "memberCount": 3,
  "createdAt": "2026-02-01T10:00:00Z",
  "updatedAt": "2026-02-01T10:00:00Z"
}
```

---

**For support, contact:** devops@cloudhealthoffice.com  
**Documentation:** https://github.com/aurelianware/cloudhealthoffice/tree/main/docs
