# Cloud Health Office - Complete EDI Workflow Suite

🎯 **Production-ready Kubernetes workflows for all major X12 EDI transactions**

## Overview

This guide covers the complete EDI integration workflow suite:
- **X12 275**: Attachment uploads (claims documentation)
- **X12 277**: Claim status response downloads
- **X12 278**: Prior authorization review requests
- **X12 837P**: Professional claims (physicians)
- **X12 837I**: Institutional claims (hospitals)
- **X12 837D**: Dental claims
- **Backend API**: Integration with Cloud Health Office backend

---

## Quick Start

### Deploy All Workflows

```bash
# 1. Deploy backend API integration
kubectl apply -f k8s/backend-api-integration.yaml

# 2. Deploy all EDI workflows
kubectl apply -f k8s/x12-277-download-job.yaml
kubectl apply -f k8s/x12-278-upload-job.yaml
kubectl apply -f k8s/x12-837-claims-jobs.yaml
kubectl apply -f k8s/x12-275-upload-job.yaml

# 3. Verify CronJobs are scheduled
kubectl get cronjobs -n cho-workflows

# 4. Test backend API integration
kubectl apply -f k8s/backend-api-integration.yaml
kubectl logs -f job/test-backend-integration -n cho-workflows
```

### Manual Testing

```bash
# Test individual workflows
kubectl create job --from=cronjob/x12-277-download-job test-277 -n cho-workflows
kubectl create job --from=cronjob/x12-278-upload-job test-278 -n cho-workflows
kubectl create job --from=cronjob/x12-837p-upload-job test-837p -n cho-workflows

# Watch logs
kubectl logs -f job/test-277 -n cho-workflows
```

---

## Workflow Details

### 1. X12 275 - Attachment Upload
**File**: [k8s/x12-275-upload-job.yaml](k8s/x12-275-upload-job.yaml)

- **Purpose**: Upload claim attachments (medical records, images, PDFs) to clearinghouses
- **Schedule**: Hourly (`0 * * * *`)
- **Direction**: Outbound (to clearinghouse)
- **SFTP Path**: `upload/275/`
- **Backend API**: 
  - GET `/api/v1/attachments/pending` - Fetch attachments needing upload
  - POST `/api/v1/attachments/submitted` - Mark as sent

**Test**:
```bash
kubectl create job --from=cronjob/x12-275-upload-job test-275-upload -n cho-workflows
kubectl logs -f job/test-275-upload -n cho-workflows
```

---

### 2. X12 277 - Claim Status Response Download
**File**: [k8s/x12-277-download-job.yaml](k8s/x12-277-download-job.yaml)

- **Purpose**: Download claim status updates from clearinghouses
- **Schedule**: Every 15 minutes (`*/15 * * * *`)
- **Direction**: Inbound (from clearinghouse)
- **SFTP Path**: `download/277/`
- **Backend API**:
  - POST `/api/v1/claims/status-updates` - Update claim statuses
  - Payload: `{transaction_type, filename, file_hash, claim_numbers, status}`

**Key Features**:
- Polls clearinghouse SFTP every 15 minutes
- Parses claim numbers from 277 EDI
- Updates backend with status changes
- Archives processed files to `download/277/processed/`
- SHA256 hash verification

**Test**:
```bash
# Create test 277 file on SFTP
kubectl run sftp-test --image=alpine --rm -i --restart=Never -- sh -c "
  apk add -q sshpass openssh-client
  sshpass -p 'sJ8p8WAsE4Es6PgMbUACErOs' sftp logicapp@sftp-service.cho-sftp.svc.cluster.local <<EOF
cd download
-mkdir 277
cd 277
EOF
"

# Run download job
kubectl create job --from=cronjob/x12-277-download-job test-277-download -n cho-workflows
kubectl logs -f job/test-277-download -n cho-workflows
```

---

### 3. X12 278 - Review Request Upload
**File**: [k8s/x12-278-upload-job.yaml](k8s/x12-278-upload-job.yaml)

- **Purpose**: Submit prior authorization review requests
- **Schedule**: Every 2 hours (`0 */2 * * *`)
- **Direction**: Outbound (to clearinghouse)
- **SFTP Path**: `upload/278/`
- **Backend API**:
  - GET `/api/v1/authorizations/pending` - Fetch pending auth requests
  - POST `/api/v1/authorizations/submitted` - Mark as submitted

**EDI Structure**:
```
ISA - Interchange Control Header
GS - Functional Group Header
ST*278 - Transaction Set (Review Request)
BHT - Beginning of Hierarchical Transaction
HL - Hierarchical Level (Requester, Provider, Subscriber)
UM - Health Care Services Review Information
HSD - Health Care Services Delivery
DTP - Date/Time Period
SE - Transaction Set Trailer
GE - Functional Group Trailer
IEA - Interchange Control Trailer
```

**Test**:
```bash
kubectl create job --from=cronjob/x12-278-upload-job test-278-upload -n cho-workflows
kubectl logs -f job/test-278-upload -n cho-workflows
```

---

### 4. X12 837P - Professional Claims
**File**: [k8s/x12-837-claims-jobs.yaml](k8s/x12-837-claims-jobs.yaml)

- **Purpose**: Submit physician/professional claims
- **Schedule**: Daily at 1 AM (`0 1 * * *`)
- **Direction**: Outbound
- **SFTP Path**: `upload/837/`
- **Backend API**:
  - GET `/api/v1/claims/unbilled?type=professional`
  - POST `/api/v1/claims/submitted`

**Use Cases**:
- Office visits (CPT 99213, 99214, etc.)
- Diagnostic services
- Outpatient procedures
- Professional component of tests

---

### 5. X12 837I - Institutional Claims
**File**: [k8s/x12-837-claims-jobs.yaml](k8s/x12-837-claims-jobs.yaml)

- **Purpose**: Submit hospital/facility claims
- **Schedule**: Daily at 2 AM (`0 2 * * *`)
- **Direction**: Outbound
- **SFTP Path**: `upload/837/`
- **Use Cases**:
  - Inpatient hospital stays
  - Emergency room visits
  - Skilled nursing facilities
  - Revenue codes (UB-04 format)

---

### 6. X12 837D - Dental Claims
**File**: [k8s/x12-837-claims-jobs.yaml](k8s/x12-837-claims-jobs.yaml)

- **Purpose**: Submit dental procedure claims
- **Schedule**: Daily at 3 AM (`0 3 * * *`)
- **Direction**: Outbound
- **SFTP Path**: `upload/837/`
- **Use Cases**:
  - Dental procedures (CDT codes)
  - Orthodontics
  - Oral surgery
  - Periodontics

---

## Backend API Integration

### Mock API (Development)

For testing without a real backend:

```bash
# Deploy mock API
kubectl apply -f k8s/backend-api-integration.yaml

# Test connectivity
kubectl logs -f deployment/backend-api-mock -n cho-workflows
```

### Production API

Update the API URL in each workflow:

```yaml
env:
  - name: BACKEND_API_URL
    value: "https://api.cloudhealthoffice.com/v1"
  - name: BACKEND_API_TOKEN
    valueFrom:
      secretKeyRef:
        name: backend-api-credentials
        key: token
```

### API Endpoints

#### Required Endpoints

**Outbound Workflows (Upload to Clearinghouse)**:
```http
GET /api/v1/attachments/pending
→ Returns: {attachments: [{attachment_id, claim_id, file_url}]}

GET /api/v1/authorizations/pending  
→ Returns: {authorizations: [{auth_number, patient_id, procedure_code}]}

GET /api/v1/claims/unbilled?type={professional|institutional|dental}
→ Returns: {claims: [{claim_id, patient, charges, cpt_codes}]}

POST /api/v1/claims/submitted
Body: {filename, type, count, hash}
→ Marks claims as submitted to clearinghouse
```

**Inbound Workflows (Download from Clearinghouse)**:
```http
POST /api/v1/claims/status-updates
Body: {transaction_type, filename, file_hash, claim_numbers[], status}
→ Updates claim status from 277 responses
```

#### Authentication

All API calls include:
```http
Authorization: Bearer <token>
Content-Type: application/json
```

Store token in Kubernetes secret:
```bash
kubectl create secret generic backend-api-credentials \
  --from-literal=token="your-api-token-here" \
  -n cho-workflows
```

---

## SFTP Directory Structure

```
/home/logicapp/
├── upload/              # Files TO clearinghouse
│   ├── 275/            # Attachments
│   ├── 278/            # Auth requests
│   └── 837/            # Claims (P/I/D)
└── download/           # Files FROM clearinghouse
    ├── 277/            # Status responses
    │   └── processed/  # Archived files
    └── 835/            # Remittance advice (future)
```

---

## Monitoring & Operations

### View All CronJobs

```bash
kubectl get cronjobs -n cho-workflows

# Example output:
NAME                    SCHEDULE          SUSPEND   ACTIVE
x12-275-upload-job      0 * * * *         False     0
x12-277-download-job    */15 * * * *      False     0
x12-278-upload-job      0 */2 * * *       False     0
x12-837p-upload-job     0 1 * * *         False     0
x12-837i-upload-job     0 2 * * *         False     0
x12-837d-upload-job     0 3 * * *         False     0
```

### View Job History

```bash
# Recent jobs
kubectl get jobs -n cho-workflows --sort-by=.metadata.creationTimestamp

# Failed jobs
kubectl get jobs -n cho-workflows --field-selector status.successful=0

# Job logs
kubectl logs job/<job-name> -n cho-workflows
```

### Suspend/Resume CronJobs

```bash
# Suspend (stop scheduling new jobs)
kubectl patch cronjob x12-277-download-job -n cho-workflows -p '{"spec":{"suspend":true}}'

# Resume
kubectl patch cronjob x12-277-download-job -n cho-workflows -p '{"spec":{"suspend":false}}'
```

### Metrics

```bash
# Count processed files
kubectl run sftp-count --image=alpine --rm -i --restart=Never -- sh -c "
  apk add -q sshpass openssh-client
  sshpass -p 'sJ8p8WAsE4Es6PgMbUACErOs' sftp logicapp@sftp-service.cho-sftp.svc.cluster.local <<'EOF'
ls -l upload/275/ | wc -l
ls -l upload/278/ | wc -l
ls -l upload/837/ | wc -l
ls -l download/277/ | wc -l
EOF
"
```

---

## Production Checklist

### Pre-Production

- [ ] Update `BACKEND_API_URL` to production URL
- [ ] Store production API token in secret
- [ ] Configure clearinghouse SFTP credentials
- [ ] Test all workflows with mock data
- [ ] Verify SFTP connectivity to real clearinghouses
- [ ] Set up IP whitelisting if required
- [ ] Configure DNS if external SFTP access needed

### Security

- [ ] Enable HIPAA audit logging
- [ ] Encrypt files at rest (Azure Disk Encryption)
- [ ] Use Azure Key Vault for secrets
- [ ] Enable Pod Security Standards
- [ ] Configure network policies (restrict SFTP egress)
- [ ] Enable Azure Monitor for container logs
- [ ] Set up alerts for failed jobs

### Compliance

- [ ] Document data retention policy (HIPAA: 6-7 years)
- [ ] Implement file archiving strategy
- [ ] Configure Business Associate Agreements with clearinghouses
- [ ] Enable access audit logs
- [ ] Implement data loss prevention monitoring

### Performance

- [ ] Tune job resources (CPU/memory limits)
- [ ] Configure horizontal pod autoscaling if needed
- [ ] Set up queue management for high volume
- [ ] Monitor SFTP transfer speeds
- [ ] Optimize EDI file batching

---

## Troubleshooting

### Common Issues

**1. SFTP Connection Refused**
```bash
# Test SFTP connectivity
kubectl run sftp-test --image=alpine --rm -i --restart=Never -- sh -c "
  apk add sshpass openssh-client
  sshpass -p 'PASSWORD' sftp -v user@host
"
```

**2. Backend API Timeout**
```bash
# Test API from within cluster
kubectl run api-test --image=alpine --rm -i --restart=Never -- sh -c "
  apk add curl
  curl -v http://backend-api.cloudhealthoffice.svc.cluster.local/health
"
```

**3. CronJob Not Running**
```bash
# Check CronJob status
kubectl describe cronjob x12-277-download-job -n cho-workflows

# Check recent job executions
kubectl get jobs -n cho-workflows --selector=job-name=x12-277-download-job
```

**4. Failed Job Debugging**
```bash
# View failed job logs
kubectl logs job/<failed-job-name> -n cho-workflows

# Get job details
kubectl describe job/<failed-job-name> -n cho-workflows

# Check pod events
kubectl get events -n cho-workflows --field-selector involvedObject.kind=Pod
```

---

## Cost Optimization

### Resource Usage (Per Month)

| Component | CPU | Memory | Storage | Est. Cost |
|-----------|-----|--------|---------|-----------|
| All CronJobs (idle) | 0.1 cores | 256Mi | - | ~$3 |
| Job executions (avg) | 0.5 cores | 512Mi | - | ~$5 |
| SFTP Storage (10GB) | - | - | 10GB | ~$2 |
| **Total** | | | | **~$10/mo** |

### Optimization Tips

1. **TTL Cleanup**: Jobs auto-delete after 24h (`ttlSecondsAfterFinished: 86400`)
2. **Concurrency**: `concurrencyPolicy: Forbid` prevents duplicate jobs
3. **Resource Limits**: Set CPU/memory requests/limits
4. **Storage**: Archive old SFTP files to cheaper blob storage

---

## Next Steps

1. **Test All Workflows**: Run manual jobs to validate each transaction
2. **Configure Production API**: Update URLs and credentials
3. **Connect to Clearinghouses**: Add real SFTP endpoints (Availity, Change Healthcare)
4. **Enable Monitoring**: Set up Prometheus/Grafana dashboards
5. **Implement 835 Downloads**: Add remittance advice processing
6. **FHIR Integration**: Convert EDI to FHIR resources for modern interop

---

## Support

- **Documentation**: [docs/](docs/)
- **Issues**: [GitHub Issues](https://github.com/aurelianware/cloudhealthoffice/issues)
- **Community**: [Discussions](https://github.com/aurelianware/cloudhealthoffice/discussions)

---

**Cloud Health Office** - The future of Azure-native EDI integration 🚀
