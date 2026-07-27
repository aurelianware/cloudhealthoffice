# Local Kubernetes Quickstart

Run the full Cloud Health Office platform on Docker Desktop Kubernetes — the same architecture, namespaces, services, and manifests used in production on Azure AKS.

## What you get

| Component | Count | Notes |
|-----------|-------|-------|
| Microservices | 25 | Claims, Members, Eligibility, Payments, FHIR, etc. |
| Portal | 1 | Blazor Server UI exposed through your local or customer-owned endpoint |
| MongoDB | 1 | StatefulSet with persistent storage |
| Redis | 1 | Data-protection key store and caching |
| Namespace | `cloudhealthoffice` | Matches production layout |

All services communicate over Kubernetes DNS (`service-name.cloudhealthoffice`) — identical to production.

---

## Prerequisites

| Tool | How to check | Install |
|------|-------------|---------|
| Docker Desktop 4.x+ | `docker --version` | [docker.com/products/docker-desktop](https://www.docker.com/products/docker-desktop) |
| Kubernetes enabled | `kubectl cluster-info` | Docker Desktop > Settings > Kubernetes > Enable |
| kubectl | `kubectl version --client` | Bundled with Docker Desktop |
| bash 4+ | `bash --version` | macOS: `brew install bash` |
| curl | `curl --version` | Pre-installed on macOS/Linux |
| jq (optional) | `jq --version` | `brew install jq` or [jqlang.github.io/jq](https://jqlang.github.io/jq/) |

> **Disk space:** First build pulls .NET 8 SDK + runtime images and compiles 25 services. Budget ~8 GB for images.

---

## 1. Enable Kubernetes in Docker Desktop

1. Open Docker Desktop > **Settings** > **Kubernetes**
2. Check **Enable Kubernetes**
3. Click **Apply & Restart** — wait for the green indicator
4. Verify:

```bash
kubectl config current-context
# Expected: docker-desktop

kubectl cluster-info
# Expected: Kubernetes control plane is running at https://127.0.0.1:6443
```

---

## 2. Configure credentials (optional)

The deploy script works out of the box with stub values — all services start and API endpoints work. For real Azure AD login and Stripe payments, create a `.env.local` file:

```bash
cp .env.local.example .env.local
# Edit .env.local with your Azure AD and Stripe test keys
```

Without `.env.local`, the portal UI loads but Azure AD sign-in won't work. API endpoints function normally with the `X-Tenant-ID` header.

---

## 3. Build and deploy

From the repo root:

```bash
./scripts/deploy-local.sh
```

This does everything:

1. **Builds** Docker images for all 25 services + portal (~10 min first time, ~2 min on rebuilds)
2. **Creates** the `cloudhealthoffice` namespace
3. **Deploys** MongoDB and Redis
4. **Creates** all Kubernetes secrets (database, Azure AD, Stripe, Redis)
5. **Seeds** demo data (tenant, member)
6. **Deploys** all microservices using the same k8s manifests as production
7. **Waits** for core services to become ready

### Flags

```bash
./scripts/deploy-local.sh              # Full build + deploy
./scripts/deploy-local.sh --skip-build # Deploy only (images already built)
./scripts/deploy-local.sh --only-build # Build images without deploying
```

### Watch the rollout

In a separate terminal:

```bash
watch kubectl get pods -n cloudhealthoffice
```

All pods should reach `Running` / `1/1 Ready` within 2-3 minutes after images are built.

---

## 4. Access the platform

Services run inside the cluster on ClusterIP. Use port-forwarding to access them locally.

### Portal (main UI)

```bash
kubectl port-forward -n cloudhealthoffice svc/portal 8080:80
```

Open [http://localhost:8080](http://localhost:8080)

### API services

Open each in a separate terminal (or append `&` to background):

```bash
# Core services
kubectl port-forward -n cloudhealthoffice svc/claims-service 5001:80
kubectl port-forward -n cloudhealthoffice svc/benefit-plan-service 5002:80
kubectl port-forward -n cloudhealthoffice svc/member-service 5003:80
kubectl port-forward -n cloudhealthoffice svc/provider-service 5004:80
kubectl port-forward -n cloudhealthoffice svc/eligibility-service 5005:80
kubectl port-forward -n cloudhealthoffice svc/payment-service 5006:80

# Infrastructure
kubectl port-forward -n cloudhealthoffice svc/mongodb 27017:27017
```

### Swagger UIs

After port-forwarding, browse to:

| Service | Swagger |
|---------|---------|
| Claims | [localhost:5001/swagger](http://localhost:5001/swagger) |
| Benefit Plans / Adjudication | [localhost:5002/swagger](http://localhost:5002/swagger) |
| Members | [localhost:5003/swagger](http://localhost:5003/swagger) |
| Providers | [localhost:5004/swagger](http://localhost:5004/swagger) |
| Eligibility | [localhost:5005/swagger](http://localhost:5005/swagger) |
| Payments | [localhost:5006/swagger](http://localhost:5006/swagger) |

---

## 5. Seed demo data and test adjudication

After port-forwarding claims (5001) and benefit-plan (5002):

```bash
CLAIMS_URL=http://localhost:5001 \
BENEFIT_URL=http://localhost:5002 \
./scripts/seed-local.sh --tenant demo
```

This seeds NCCI edits, creates a benefit plan, submits a claim, runs adjudication, executes a payment batch, and downloads an 835 ERA file — the full claims lifecycle.

For a step-by-step walkthrough with curl commands, see [local-claims-quickstart.md](../api/quickstarts/local-claims-quickstart.md).

---

## 6. Verify system health

### Check all pods

```bash
kubectl get pods -n cloudhealthoffice
```

Expected: all pods `Running`, `1/1` ready.

### Hit health endpoints

```bash
for svc in claims-service member-service benefit-plan-service eligibility-service \
           payment-service provider-service portal; do
  STATUS=$(kubectl exec -n cloudhealthoffice deploy/$svc -- \
    curl -sf http://localhost:8080/health/live 2>/dev/null || echo "DOWN")
  echo "$svc: $STATUS"
done
```

### View logs

```bash
# Portal logs
kubectl logs -n cloudhealthoffice -l app=portal --tail=50

# Claims service logs
kubectl logs -n cloudhealthoffice -l app=claims-service --tail=50

# All services with errors
kubectl logs -n cloudhealthoffice --all-containers --tail=20 | grep -i error
```

---

## How this matches production

| Aspect | Local (Docker Desktop) | Production (Azure AKS) |
|--------|----------------------|----------------------|
| Namespace | `cloudhealthoffice` | `cloudhealthoffice` |
| Service DNS | `svc.cloudhealthoffice` | `svc.cloudhealthoffice` |
| K8s manifests | Same files, `imagePullPolicy` patched to `IfNotPresent` | `Always` (pulls from ACR) |
| Container port | 8080 | 8080 |
| Secrets | Local stubs via `deploy-local.sh` | Azure Key Vault |
| Database | MongoDB StatefulSet (single node) | Azure Cosmos DB (MongoDB API) |
| Redis | Single pod | Azure Cache for Redis |
| Ingress/TLS | None (port-forward) | NGINX + cert-manager + Let's Encrypt |
| Replicas | 1 per service | 2+ with HPA |
| Image registry | Local Docker | Azure ACR / GitHub GHCR |

---

## Common tasks

### Rebuild and redeploy a single service

```bash
# Rebuild just the claims service
docker build -t clouhealthoffice.azurecr.io/cloudhealthoffice-claims-service:latest \
  -f src/services/claims-service/Dockerfile .

# Restart the deployment to pick up the new image
kubectl rollout restart deployment/claims-service -n cloudhealthoffice
kubectl rollout status deployment/claims-service -n cloudhealthoffice
```

### Connect to MongoDB

```bash
kubectl port-forward -n cloudhealthoffice svc/mongodb 27017:27017

# In another terminal:
mongosh "mongodb://admin:localdev123@localhost:27017/?authSource=admin"
```

### View all services and endpoints

```bash
kubectl get svc -n cloudhealthoffice
```

### Scale a service

```bash
kubectl scale deployment/claims-service -n cloudhealthoffice --replicas=3
```

### Apply a manifest change

After editing a k8s manifest (e.g. ConfigMap or deployment):

```bash
kubectl apply -f src/services/claims-service/k8s/claims-service-deployment.yaml
```

### Use Azure Service Bus for local claims adjudication

Claims adjudication uses the in-memory message bus by default. To exercise the
durable Azure Service Bus path from local Kubernetes, add these non-secret
settings to `.env.local`:

```bash
LOCAL_SERVICEBUS_NAMESPACE=your-service-bus-namespace
LOCAL_SERVICEBUS_RESOURCE_GROUP=your-resource-group
LOCAL_SERVICEBUS_LOCATION=your-azure-region
```

Then run the normal deployment:

```bash
./scripts/deploy-local.sh --skip-build
```

The deployment uses your current Azure CLI login to provision the claims topic
and subscription, creates a namespace authorization rule with only `Listen` and
`Send` rights, and installs its connection string as `servicebus-secret` in the
`cloudhealthoffice` namespace. The credential is not stored in `.env.local`.
Remove all three settings to return to the in-memory message bus.

---

## Troubleshooting

### Pods stuck in ImagePullBackOff

Images are built locally but the manifest says `imagePullPolicy: Always`. The deploy script patches this automatically, but if you applied a manifest manually:

```bash
kubectl patch deployment claims-service -n cloudhealthoffice \
  -p '{"spec":{"template":{"spec":{"containers":[{"name":"claims-service","imagePullPolicy":"IfNotPresent"}]}}}}'
```

### Pods in CrashLoopBackOff

Usually a missing secret or MongoDB not ready yet:

```bash
# Check what's failing
kubectl describe pod -n cloudhealthoffice -l app=claims-service
kubectl logs -n cloudhealthoffice -l app=claims-service --previous

# Verify secrets exist
kubectl get secrets -n cloudhealthoffice
```

### MongoDB not starting

```bash
kubectl describe statefulset mongodb -n cloudhealthoffice
kubectl logs -n cloudhealthoffice mongodb-0
```

If PVC issues on Docker Desktop, try resetting the Kubernetes cluster: Docker Desktop > Settings > Kubernetes > Reset Kubernetes Cluster.

### Port-forward dies or hangs

Port-forwards drop when pods restart. Re-run the command, or use a tool like [kubefwd](https://github.com/txn2/kubefwd) for automatic forwarding.

### Services can't reach each other

Verify DNS resolution inside the cluster:

```bash
kubectl run -n cloudhealthoffice dns-test --rm -it --image=busybox -- \
  nslookup claims-service.cloudhealthoffice
```

### Out of memory / Docker Desktop slow

The full platform runs 30+ pods. Allocate at least:
- **CPU:** 6 cores
- **Memory:** 16 GB minimum, 24 GB recommended (Settings > Resources)

---

## Tear down

### Stop everything but keep data

```bash
kubectl delete namespace cloudhealthoffice
```

Data in MongoDB PVCs persists. Re-run `./scripts/deploy-local.sh --skip-build` to bring it back.

### Full reset (delete everything including data)

```bash
kubectl delete namespace cloudhealthoffice
docker volume prune -f
```

---

## Next steps

- **Claims quickstart:** [local-claims-quickstart.md](../api/quickstarts/local-claims-quickstart.md) — step-by-step claim adjudication with curl
- **HTTPS ingress:** [QUICKSTART-HTTPS.md](../infrastructure/k8s/QUICKSTART-HTTPS.md) — add TLS if exposing externally
- **Production deployment:** See the [CI/CD pipeline](.github/workflows/deploy-azure-aks.yml) for AKS deployment
