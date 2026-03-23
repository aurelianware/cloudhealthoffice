# 834 Enrollment Import Pipeline - Quick Deployment Checklist

## Pre-Deployment Checklist

- [ ] **Cosmos DB Setup**
  - [ ] Database `CloudHealthOffice` created
  - [ ] Container `Members` created (partition key: `/id`, 400 RU/s)
  - [ ] Container `Coverage` created (partition key: `/id`, 400 RU/s)
  - [ ] Container `Sponsors` created (partition key: `/id`, 400 RU/s)
  - [ ] Cosmos DB endpoint retrieved
  - [ ] Cosmos DB primary key retrieved

- [ ] **Kubernetes Secrets**
  - [ ] `database-secret` created in `cloudhealthoffice` namespace
    ```bash
    kubectl create secret generic database-secret \
      --namespace cloudhealthoffice \
      --from-literal=endpoint="<cosmos-endpoint>" \
      --from-literal=key="<cosmos-key>"
    ```
  - [ ] `sftp-creds` created in `cloudhealthoffice` namespace
    ```bash
    kubectl create secret generic sftp-creds \
      --namespace cloudhealthoffice \
      --from-literal=username="<sftp-username>" \
      --from-literal=password="<sftp-password>"
    ```
  - [ ] `kafka-config` ConfigMap exists (bootstrap-servers)
  - [ ] `ghcr-secret` image pull secret exists

- [ ] **Kafka Topics**
  - [ ] `enrollment-import` topic created (3 partitions, 30-day retention)
    ```bash
    kubectl apply -f kafka/topics.yaml
    kubectl get kafkatopics -n kafka enrollment-import
    ```

- [ ] **SFTP Server**
  - [ ] Directory `/inbound/enrollment` created
  - [ ] Directory `/archive/834` created
  - [ ] Permissions set (770 or rwxrwx---)
  - [ ] Firewall rules allow Kubernetes pod IPs
  - [ ] SSH/SFTP credentials valid

---

## Deployment Steps

### 1. Build Docker Images

- [ ] **X12 834 Parser**
  ```bash
  docker build -t ghcr.io/aurelianware/cloudhealthoffice-x12-834-parser:latest \
    containers/x12-834-parser
  docker push ghcr.io/aurelianware/cloudhealthoffice-x12-834-parser:latest
  ```

- [ ] **Enrollment Import Service**
  ```bash
  docker build -t ghcr.io/aurelianware/cloudhealthoffice-enrollment-import-service:latest \
    services/enrollment-import-service
  docker push ghcr.io/aurelianware/cloudhealthoffice-enrollment-import-service:latest
  ```

  **OR** wait for GitHub Actions to build automatically (push to `main` branch)

### 2. Deploy Enrollment Import Service

- [ ] **Apply Kubernetes deployment**
  ```bash
  kubectl apply -f services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml
  ```

- [ ] **Verify deployment**
  ```bash
  kubectl get pods -n cloudhealthoffice -l app=enrollment-import-service
  # Expected: 2 pods Running
  ```

- [ ] **Check service**
  ```bash
  kubectl get svc -n cloudhealthoffice enrollment-import-service
  # Expected: ClusterIP service on port 80
  ```

- [ ] **Check HPA**
  ```bash
  kubectl get hpa -n cloudhealthoffice enrollment-import-service-hpa
  # Expected: 2/10 replicas, targets: CPU 70%, Memory 80%
  ```

- [ ] **Verify logs**
  ```bash
  kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=50
  # Expected: "Application started" message, no errors
  ```

- [ ] **Test health endpoint**
  ```bash
  kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8080:80
  curl http://localhost:8080/health
  # Expected: HTTP 200 OK
  ```

### 3. Deploy Argo Workflow

- [ ] **Apply CronWorkflow**
  ```bash
  kubectl apply -f argo-workflows/x12-834-enrollment-import.yaml
  ```

- [ ] **Verify CronWorkflow**
  ```bash
  kubectl get cronworkflows -n cloudhealthoffice x12-834-enrollment-import
  # Expected: SCHEDULE=*/10 * * * *, SUSPEND=false
  ```

- [ ] **Check workflow template**
  ```bash
  argo cron get x12-834-enrollment-import -n cloudhealthoffice
  # Expected: 4 steps (fetch, parse, import, archive)
  ```

---

## Testing Checklist

### Test 1: Manual API Call

- [ ] **Port-forward enrollment-import-service**
  ```bash
  kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8080:80
  ```

- [ ] **Send test enrollment (prepare parsed 834 JSON)**
  ```bash
  curl -X POST http://localhost:8080/api/v1/enrollment/import \
    -H "Content-Type: application/json" \
    -H "X-Tenant-ID: tenant-test" \
    -d @test-834-parsed.json
  ```

- [ ] **Verify response**
  - `successCount > 0`
  - `failedCount = 0`
  - `membersCreated > 0`

### Test 2: Upload Sample 834 File

- [ ] **Upload sample file to SFTP**
  ```bash
  sftp <username>@<sftp-host>
  put test-x12-834-enrollment-sample.edi /inbound/enrollment/test-enrollment-20260201.edi
  exit
  ```

- [ ] **Trigger workflow manually**
  ```bash
  argo submit --from cronwf/x12-834-enrollment-import -n cloudhealthoffice \
    --parameter sftp-host="<sftp-host>" \
    --parameter sftp-path="/inbound/enrollment" \
    --parameter tenant-id="tenant-test"
  ```

- [ ] **Watch workflow execution**
  ```bash
  argo watch @latest -n cloudhealthoffice
  # Expected: All 4 steps succeed (fetch → parse → import → archive)
  ```

- [ ] **View logs**
  ```bash
  argo logs @latest -n cloudhealthoffice
  ```

- [ ] **Check workflow output**
  ```bash
  argo get @latest -n cloudhealthoffice -o yaml | grep -A 10 "outputs:"
  # Expected:
  # - totalFiles: 1
  # - totalEnrollments: 3
  # - membersCreated: 6
  # - membersTerminated: 1
  ```

### Test 3: Verify Data in Cosmos DB

- [ ] **Query Members container**
  ```bash
  # Azure Portal → Cosmos DB → Data Explorer → CloudHealthOffice → Members
  SELECT * FROM c WHERE c.tenantId = "tenant-test"
  # Expected: 6 members (3 subscribers + 3 dependents)
  ```

- [ ] **Verify member details**
  - [ ] John Smith (subscriber, active, 2 dependents)
  - [ ] Jane Smith (dependent, spouse)
  - [ ] Michael Smith (dependent, child)
  - [ ] Sarah Johnson (subscriber, active, 1 dependent)
  - [ ] Robert Johnson (dependent, spouse)
  - [ ] Robert Williams (subscriber, terminated)

- [ ] **Query Coverage container**
  ```bash
  SELECT * FROM c WHERE c.tenantId = "tenant-test"
  # Expected: 6+ coverage records (health, dental, vision)
  ```

- [ ] **Verify coverage details**
  - [ ] John Smith: HLT (PPO) + DEN (Basic) + VIS (Standard)
  - [ ] Sarah Johnson: HLT (HMO)
  - [ ] Robert Williams: Coverage terminated

- [ ] **Query Sponsors container**
  ```bash
  SELECT * FROM c WHERE c.tenantId = "tenant-test"
  # Expected: 1 sponsor (Acme Corporation)
  ```

- [ ] **Verify sponsor details**
  - [ ] Name: Acme Corporation
  - [ ] Federal Tax ID: 123456789
  - [ ] Group Number: GRP0001
  - [ ] Member Count: 3

### Test 4: Verify SFTP Archive

- [ ] **Check SFTP archive directory**
  ```bash
  sftp <username>@<sftp-host>
  ls -la /archive/834/
  # Expected: test-enrollment-20260201.edi moved from /inbound/enrollment
  ```

### Test 5: Monitor Kafka Topic

- [ ] **View enrollment-import topic messages**
  ```bash
  kubectl exec -it -n kafka cloudhealthoffice-kafka-0 -- \
    /opt/kafka/bin/kafka-console-consumer.sh \
    --bootstrap-server localhost:9092 \
    --topic enrollment-import \
    --from-beginning \
    --max-messages 10
  # Expected: Enrollment import events (MemberEnrolled, MemberTerminated)
  ```

---

## Post-Deployment Validation

### Monitoring

- [ ] **Prometheus metrics available**
  ```bash
  kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 8080:80
  curl http://localhost:8080/metrics | grep enrollment
  # Expected:
  # - enrollment_imports_total
  # - enrollment_members_created_total
  # - enrollment_members_terminated_total
  ```

- [ ] **Grafana dashboards showing data** (if configured)

- [ ] **Logs aggregated in ELK/Loki** (if configured)

### Performance

- [ ] **HPA scaling working**
  ```bash
  # Generate load (upload 100 834 files)
  kubectl get hpa -n cloudhealthoffice enrollment-import-service-hpa --watch
  # Expected: Replicas increase as CPU/memory usage increases
  ```

- [ ] **End-to-end latency <2 minutes**
  - Time from file upload to Cosmos DB write

- [ ] **Cosmos DB RU consumption <100 RUs per enrollment**

### Reliability

- [ ] **CronWorkflow running on schedule**
  ```bash
  argo cron get x12-834-enrollment-import -n cloudhealthoffice
  # Expected: LAST RUN and NEXT RUN times shown
  ```

- [ ] **Dead letter queue empty** (no failed imports)
  ```bash
  kubectl exec -it -n kafka cloudhealthoffice-kafka-0 -- \
    /opt/kafka/bin/kafka-console-consumer.sh \
    --bootstrap-server localhost:9092 \
    --topic dead-letter-queue \
    --from-beginning \
    --max-messages 10
  # Expected: No messages (or only test failures)
  ```

- [ ] **No errors in pod logs**
  ```bash
  kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=100 | grep -i error
  # Expected: No errors
  ```

---

## Troubleshooting Checklist

### Issue: Pods not starting

- [ ] Check image pull secret
  ```bash
  kubectl get secret ghcr-secret -n cloudhealthoffice
  ```

- [ ] Check pod events
  ```bash
  kubectl describe pod -n cloudhealthoffice -l app=enrollment-import-service
  ```

- [ ] Check image exists
  ```bash
  docker pull ghcr.io/aurelianware/cloudhealthoffice-enrollment-import-service:latest
  ```

### Issue: Health checks failing

- [ ] Check Cosmos DB connectivity
  ```bash
  kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=50 | grep -i cosmos
  ```

- [ ] Verify Cosmos DB secret
  ```bash
  kubectl get secret database-secret -n cloudhealthoffice -o yaml
  ```

- [ ] Test Cosmos DB endpoint manually
  ```bash
  curl https://<cosmos-account>.documents.azure.com/dbs
  # Expected: 401 Unauthorized (means endpoint reachable)
  ```

### Issue: Workflow fails at fetch-from-sftp

- [ ] Verify SFTP credentials
  ```bash
  kubectl get secret sftp-creds -n cloudhealthoffice -o yaml
  ```

- [ ] Test SFTP connection manually
  ```bash
  sftp <username>@<sftp-host>
  ls /inbound/enrollment
  ```

- [ ] Check SFTP host reachability from pod
  ```bash
  kubectl run -it --rm debug --image=alpine --restart=Never -- \
    nc -zv <sftp-host> 22
  ```

### Issue: Workflow fails at parse-834-files

- [ ] View parser logs
  ```bash
  argo logs <workflow-name> -n cloudhealthoffice --container parse-834-files
  ```

- [ ] Check for .error.json files in workflow output

- [ ] Validate 834 file structure (ISA/GS/ST segments present)

### Issue: Workflow fails at import-to-cosmos

- [ ] View import service logs
  ```bash
  argo logs <workflow-name> -n cloudhealthoffice --container import-to-cosmos
  ```

- [ ] Check enrollment-import-service pod logs
  ```bash
  kubectl logs -n cloudhealthoffice -l app=enrollment-import-service --tail=100
  ```

- [ ] Verify X-Tenant-ID header passed correctly
  ```bash
  argo get <workflow-name> -n cloudhealthoffice -o yaml | grep -i tenant
  ```

---

## Rollback Plan

If deployment fails or causes issues:

- [ ] **Stop CronWorkflow**
  ```bash
  kubectl patch cronworkflow x12-834-enrollment-import -n cloudhealthoffice \
    -p '{"spec":{"suspend":true}}'
  ```

- [ ] **Scale down enrollment-import-service**
  ```bash
  kubectl scale deployment enrollment-import-service -n cloudhealthoffice --replicas=0
  ```

- [ ] **Investigate issue** (check logs, metrics, Cosmos DB data)

- [ ] **Restore from backup** (if Cosmos DB data corrupted)
  ```bash
  # Use Cosmos DB point-in-time restore
  az cosmosdb sql database restore --help
  ```

- [ ] **Re-deploy after fix**
  ```bash
  # Fix code, rebuild images, re-apply Kubernetes manifests
  kubectl apply -f services/enrollment-import-service/k8s/enrollment-import-service-deployment.yaml
  kubectl apply -f argo-workflows/x12-834-enrollment-import.yaml
  ```

---

## Success Criteria

Deployment is successful when:

- ✅ **All pods Running**: `kubectl get pods -n cloudhealthoffice -l app=enrollment-import-service`
- ✅ **Health checks passing**: `curl http://localhost:8080/health` → HTTP 200
- ✅ **CronWorkflow scheduled**: `argo cron get x12-834-enrollment-import -n cloudhealthoffice`
- ✅ **Sample file processed**: 3 enrollments imported, 6 members created, 1 terminated
- ✅ **Data in Cosmos DB**: Members, Coverage, Sponsors containers populated
- ✅ **No errors in logs**: `kubectl logs -n cloudhealthoffice -l app=enrollment-import-service`
- ✅ **Prometheus metrics available**: `/metrics` endpoint responding
- ✅ **HPA scaling working**: Pods scale 2-10 based on load

---

## Next Steps After Deployment

- [ ] **Update existing services** to read from Cosmos DB (Member, Coverage, Sponsor services)
- [ ] **Deploy to production** (blue-green deployment with gradual rollout)
- [ ] **Onboard first employer** (real 834 file, production tenant)
- [ ] **Monitor for 7 days** (ensure CronWorkflow runs reliably)
- [ ] **Document runbooks** for operations team
- [ ] **Set up alerts** (PagerDuty/Opsgenie for workflow failures)

---

**Deployment Time Estimate:** 30-60 minutes  
**Rollback Time Estimate:** 5 minutes  
**Support Contact:** devops@cloudhealthoffice.com
