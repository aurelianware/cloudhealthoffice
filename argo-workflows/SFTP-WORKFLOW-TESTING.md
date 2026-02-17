# SFTP Integration Workflow Testing Guide

This guide demonstrates deploying and running Argo Workflows that interact with the Kubernetes-hosted SFTP server.

## Quick Start

### 1. Deploy the Test Workflow

```bash
# Apply the workflow template
kubectl apply -f argo-workflows/sftp-test-workflow.yaml

# Verify deployment
kubectl get workflowtemplate -n cloudhealthoffice
```

### 2. Run the Workflow

```bash
# Submit workflow manually
argo submit argo-workflows/sftp-test-workflow.yaml -n cloudhealthoffice --watch

# Or run with custom parameters
argo submit argo-workflows/sftp-test-workflow.yaml \
  -n cloudhealthoffice \
  -p sftp-host="sftp-service.cho-sftp.svc.cluster.local" \
  -p test-filename="custom-test-$(date +%s).x12" \
  --watch
```

### 3. Monitor Workflow Progress

```bash
# List recent workflows
argo list -n cloudhealthoffice

# Watch specific workflow
argo watch sftp-test-workflow-xxxxx -n cloudhealthoffice

# Get workflow logs
argo logs sftp-test-workflow-xxxxx -n cloudhealthoffice

# View in UI
argo server -n cloudhealthoffice
# Then open: http://localhost:2746
```

## Workflow Steps

The test workflow performs a complete upload/download cycle:

```
1. generate-test-file     → Creates sample X12 278 EDI file
2. upload-to-sftp        → Pushes file to /upload/278/
3. verify-upload         → Confirms file exists on SFTP
4. download-from-sftp    → Retrieves file from SFTP
5. verify-download       → Validates SHA256 hash match
```

## Expected Output

Successful workflow run:

```
NAME                      STATUS      AGE   DURATION   PRIORITY   MESSAGE
sftp-test-workflow-abc12  Succeeded   2m    45s        0          

Steps:
✓ generate-test-file      Succeeded   2m    5s
✓ upload-to-sftp         Succeeded   2m    12s
✓ verify-upload          Succeeded   2m    8s
✓ download-from-sftp     Succeeded   1m    10s
✓ verify-download        Succeeded   1m    10s
```

## Troubleshooting

### Workflow Fails at Upload Step

**Check SFTP server is running:**
```bash
kubectl get pods -n cho-sftp
kubectl logs -n cho-sftp deployment/sftp-server -f
```

**Verify credentials:**
```bash
kubectl get secret sftp-users -n cho-sftp -o jsonpath='{.data.users\.conf}' | base64 -d
```

**Test SFTP connectivity:**
```bash
kubectl run sftp-test --image=alpine --rm -i --restart=Never -- sh -c "
  apk add --no-cache sshpass openssh-client
  echo 'Testing SFTP connection...'
  sshpass -p 'YOUR_PASSWORD' sftp -o StrictHostKeyChecking=no logicapp@sftp-service.cho-sftp.svc.cluster.local <<EOF
ls
bye
EOF
"
```

### Workflow Stuck in Pending

**Check Argo Workflows controller:**
```bash
kubectl get pods -n argo
kubectl logs -n argo deployment/workflow-controller
```

**Check service account permissions:**
```bash
kubectl get sa argo-workflow-sa -n cloudhealthoffice
kubectl describe sa argo-workflow-sa -n cloudhealthoffice
```

### Permission Denied on SFTP

**Verify user directory exists:**
```bash
kubectl exec -n cho-sftp deployment/sftp-server -- ls -la /home/logicapp/
```

**Check directory permissions:**
```bash
kubectl exec -n cho-sftp deployment/sftp-server -- ls -la /home/logicapp/upload/
```

**Fix permissions if needed:**
```bash
kubectl exec -n cho-sftp deployment/sftp-server -- chown -R logicapp:logicapp /home/logicapp/
```

## Automated Testing

The workflow includes a CronWorkflow that runs daily at 2 AM:

```bash
# View scheduled workflows
kubectl get cronworkflow -n cloudhealthoffice

# Manually trigger a cron workflow
kubectl create job --from=cronjob/sftp-test-schedule sftp-manual-test -n cloudhealthoffice
```

## Production Workflows

For production EDI processing, see:

- [x12-278-ingest.yaml](./x12-278-ingest.yaml) - Authorization requests from clearinghouses
- [x12-277-rfai.yaml](./x12-277-rfai.yaml) - Status responses to clearinghouses
- [x12-275-ingest.yaml](./x12-275-ingest.yaml) - Prior auth attachments

### Example: Upload 278 to Clearinghouse

```bash
# Submit workflow with production parameters
argo submit argo-workflows/x12-278-ingest.yaml \
  -n cloudhealthoffice \
  -p sftp-host="sftp-service.cho-sftp.svc.cluster.local" \
  -p sftp-folder="/upload/278" \
  -p claims-backend-api-endpoint="http://claims-api.cloudhealthoffice.svc.cluster.local/api/v1/claims/278" \
  --watch
```

### Example: Download 277 Status from Clearinghouse

```bash
# Poll for new status files
argo submit argo-workflows/x12-277-rfai.yaml \
  -n cloudhealthoffice \
  -p sftp-host="sftp-service.cho-sftp.svc.cluster.local" \
  -p sftp-folder="/download/277" \
  -p file-pattern="STATUS_*.x12" \
  --watch
```

## Integration with Logic Apps

If you prefer Azure Logic Apps over Argo Workflows:

1. **Configure API Connection:**
   ```bash
   ./scripts/configure-sftp-connection.sh
   ```

2. **Update Logic Apps workflows:**
   - Edit [logicapps/ingest278/workflow.json](../logicapps/ingest278/workflow.json)
   - Add SFTP connector actions for upload/download
   - Reference connection ID from `configure-sftp-connection.sh` output

3. **Deploy Logic Apps:**
   ```bash
   ./scripts/deploy-workflows.sh
   ```

## Monitoring & Alerting

### View Workflow Metrics

```bash
# Success rate (last 24h)
argo list -n cloudhealthoffice --completed --since 24h | grep Succeeded | wc -l
argo list -n cloudhealthoffice --completed --since 24h | wc -l

# Average duration
argo list -n cloudhealthoffice --completed --since 24h -o json | jq '.[] | .status.finishedAt - .status.startedAt'
```

### Setup Prometheus Alerts

Add to `monitoring/prometheus-rules.yaml`:

```yaml
- alert: SFTPWorkflowFailureRate
  expr: |
    sum(rate(argo_workflow_status_phase{phase="Failed",namespace="cloudhealthoffice"}[5m])) 
    / 
    sum(rate(argo_workflow_status_phase{namespace="cloudhealthoffice"}[5m])) 
    > 0.1
  for: 10m
  labels:
    severity: warning
  annotations:
    summary: "High SFTP workflow failure rate"
    description: "{{ $value | humanizePercentage }} of SFTP workflows failing"
```

## Best Practices

### Security

- **Never commit credentials:** Use Kubernetes Secrets for SFTP passwords
- **Use Key Vault references:** For Azure-hosted workflows, reference Key Vault
- **Rotate passwords regularly:** Update `sftp-users` secret monthly
- **Enable audit logging:** Track all SFTP access

### Reliability

- **Implement retries:** Use workflow retry strategy (already configured)
- **Set timeouts:** Prevent hung workflows (activeDeadlineSeconds)
- **Monitor disk usage:** SFTP PVC can fill up, implement cleanup jobs
- **Test failover:** Simulate SFTP server restart, verify workflows recover

### Performance

- **Batch small files:** Reduce overhead by bundling multiple claims
- **Use compression:** Gzip EDI files before upload
- **Parallel processing:** Use workflow parallelism for multiple files
- **Archive old files:** Move processed files to cold storage (Azure Blob/S3)

## Cleanup

### Delete Test Workflow

```bash
# Remove workflow template
kubectl delete workflowtemplate sftp-test-workflow -n cloudhealthoffice

# Remove cron workflow
kubectl delete cronworkflow sftp-test-schedule -n cloudhealthoffice

# Clean up completed workflows
argo delete -n cloudhealthoffice --completed
```

### Delete All SFTP Infrastructure

```bash
# Remove SFTP server and data
kubectl delete namespace cho-sftp

# This deletes:
# - SFTP deployment
# - PersistentVolumeClaim (and data!)
# - LoadBalancer IP
# - Secrets (passwords, SSH keys)
```

## Next Steps

1. ✅ Test workflow deployed
2. ⬜ Customize EDI templates for your payers
3. ⬜ Connect to real clearinghouse IPs (Availity, Change HC)
4. ⬜ Set up DNS for SFTP server (optional)
5. ⬜ Configure IP whitelisting for production
6. ⬜ Deploy production workflows (278, 277, 275, 837)
7. ⬜ Enable monitoring and alerting
8. ⬜ Schedule backup jobs for SFTP data

## Related Documentation

- [SFTP Integration Guide](../docs/SFTP-INTEGRATION-GUIDE.md)
- [SFTP Architecture](../docs/SFTP-ARCHITECTURE.md)
- [Argo Workflows Documentation](https://argo-workflows.readthedocs.io/)
- [X12 278 Specification](../docs/X12-278-SPECIFICATION.md)
