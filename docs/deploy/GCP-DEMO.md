# Cloud Health Office demo on Google Cloud (GKE)

This runbook stands up Cloud Health Office on GKE (a single-node Standard cluster by default, or Autopilot), runs a 25-minute demo, and tears it down. The same services that run on Azure AKS run here without an application rewrite. Only the infrastructure, manifests rendering and secrets differ.

> **Demo-grade.** Synthetic data only. The cluster trusts the repository's public development signing key and has no public ingress; it is reached by `kubectl port-forward`, or optionally at `demo.cloudhealthoffice.com` through a Cloudflare Tunnel behind Cloudflare Access (see [GCP-PUBLIC-HOSTING-PLAN.md](GCP-PUBLIC-HOSTING-PLAN.md); never without Access). It is not a HIPAA production environment. See [What this is and isn't](#what-this-is-and-isnt).

## Architecture

```mermaid
flowchart LR
  presenter["Presenter laptop<br/>kubectl port-forward"]
  gha["GitHub Actions<br/>deploy-gcp-gke.yml"]
  subgraph gcp["Google Cloud project"]
    ar["Artifact Registry<br/>cho"]
    gcs[("Cloud Storage<br/>documents bucket")]
    subgraph gke["GKE: cho-demo"]
      subgraph ns["namespace cloudhealthoffice"]
        portal["Portal (Blazor)"]
        svc["~36 .NET 8 services<br/>claims, benefit plan, FHIR, ..."]
        mongo[("MongoDB")]
        redis[("Redis")]
      end
      subgraph kns["namespace kafka"]
        kafka["Kafka (Strimzi)<br/>3 KRaft brokers"]
      end
    end
  end
  anthropic["Anthropic API<br/>advisory claims examiner"]
  gha -- "keyless (Workload Identity Federation)" --> ar
  gha -- deploy --> gke
  ar -- images --> gke
  presenter --> portal
  presenter --> svc
  portal --> svc
  svc --> mongo
  svc --> redis
  svc --> kafka
  svc -- "Workload Identity" --> gcs
  svc -. "optional" .-> anthropic
```

| Piece | Where it is defined |
|---|---|
| Cluster, image repository, documents bucket, keyless GitHub access, budget alert | [`infrastructure/gcp/`](../../infrastructure/gcp/README.md) (Terraform) |
| Build and deploy | [`.github/workflows/deploy-gcp-gke.yml`](../../.github/workflows/deploy-gcp-gke.yml), [`scripts/deploy-gke.sh`](../../scripts/deploy-gke.sh) |
| GKE-specific manifest changes | [`deploy/gcp/render_manifests.py`](../../deploy/gcp/render_manifests.py) (the service manifests themselves are shared with AKS and local) |
| Kafka | [`deploy/gcp/kafka/kafka-cluster.yaml`](../../deploy/gcp/kafka/kafka-cluster.yaml), topics from [`infrastructure/kafka/topics.yaml`](../../infrastructure/kafka/topics.yaml) |
| Demo scripts | [`scripts/demo/gcp/`](../../scripts/demo/gcp/README.md) |

## Set up (once)

1. **Create the infrastructure.** In a dedicated Google Cloud project with billing enabled:

   ```bash
   cd infrastructure/gcp
   terraform init
   terraform apply -var="project_id=YOUR_PROJECT_ID"
   ```

   Optional: `-var="billing_account_id=XXXXXX-XXXXXX-XXXXXX"` adds a budget alert. The cluster takes about 10 minutes.

2. **Configure GitHub** (Settings → Secrets and variables → Actions) from the Terraform outputs. The table in [`infrastructure/gcp/README.md`](../../infrastructure/gcp/README.md#configure-github) maps each output to a name. Add `ANTHROPIC_API_KEY` as a secret to enable the advisory AI claims examiner.

3. **Deploy.** Every merge to `main` deploys automatically (docs-only changes are skipped). To deploy by hand: Actions → **Deploy demo to GKE** → Run workflow. The first run builds 37 images in parallel and takes roughly 20–40 minutes; later runs use the build cache. To redeploy without rebuilding, enter an existing image tag (a commit SHA). Merges that land while a deploy is running queue behind it, and only the newest one deploys.
   - **Pause automatic deploys** (for example before a live demo, since a deploy restarts pods): set the repository variable `GKE_AUTO_DEPLOY` to `false`. Delete it or set it to `true` to resume. Manual runs work either way.
   - **AI claims examiner.** On for automatic runs, and for manual runs unless you untick `ai_examiner`. When it's on, the deploy fails if the `ANTHROPIC_API_KEY` secret is missing, and after rollout the examiner makes one real call to Claude with its own settings. The deploy fails, with the API's reason, if that call doesn't succeed (revoked or mistyped key, no credit, retired model). A deploy doesn't reset the demo data; `scripts/demo/gcp/reset-demo.sh --yes` clears the demo tenant's claims activity before a fresh `run-mcc.sh` load.

4. **Connect and check.** On the presenter's laptop:

   ```bash
   gcloud container clusters get-credentials cho-demo --location us-west1-a --project YOUR_PROJECT_ID   # --location us-west1 for Autopilot
   ./scripts/demo/gcp/port-forward.sh               # leave running in its own terminal
   python3 scripts/demo/gcp/cho_demo.py smoke       # must end "All smoke checks passed."
   ```

5. **Load demo results.** Run the benchmark once before the demo so the portal has run history, then again live if time allows:

   ```bash
   GAR_REPOSITORY=<vars.GAR_REPOSITORY> IMAGE_TAG=<deployed tag> ./scripts/demo/gcp/run-mcc.sh
   ```

## Demo script (25 minutes)

Rehearse twice. Record a backup video of the claim, examiner and benchmark segments, and keep saved output of each command in case the cluster misbehaves.

| Minutes | Segment | Show | Command or URL |
|---|---|---|---|
| 0–2 | Frame | Cloud Health Office as the core and data layer under a payer's products. All data is synthetic and labeled. | — |
| 2–6 | Architecture | Service map, tenant model, events through Kafka, FHIR model, adapters and operating modes. It is running on GKE now. | This page's diagram; `kubectl get pods -n cloudhealthoffice` |
| 6–7 | How it was built | Multi-agent AI development with scoped tasks, acceptance criteria and independent audits. | — |
| 7–13 | A claim, live | A paid claim with its adjudication detail, then a pended NCCI claim with the AI examiner's advisory recommendation (disposition, confidence, rationale, citations) and its audit record. The claim status is unchanged; a human decides. | `python3 scripts/demo/gcp/cho_demo.py claims`; portal at http://localhost:8080 |
| 13–16 | Scale | Million Claim Challenge results in the Batch Adjudication page, plus a small live run. Published full-corpus figures (1,000,000 claims, 155.89 claims/sec, zero dead letters) are local Kubernetes benchmarks, not GKE production capacity. | Portal → Mass Adjudication; `run-mcc.sh` |
| 16–20 | CMS-0057-F | PAS `$submit` approve, deny and pend; adapter-status labels; the Da Vinci interoperability evidence. | `python3 scripts/demo/gcp/cho_demo.py adapter-status`; `FHIR_URL=http://localhost:5023 TENANT_ID=demo FHIR_TOKEN="$(python3 scripts/demo/gcp/cho_demo.py token)" ./scripts/demo/cms-0057-f-demo.sh`; [`docs/interop/davinci.md`](../interop/davinci.md) |
| 20–23 | Plugging into a plan's existing core | Tenant configuration, the operating-mode matrix, the core administration adapter QRE and the implementation blueprint. Shown as configuration and documents; legacy-core adapters are built per engagement. | [`docs/features/questionnaires/`](../features/questionnaires/), [`docs/implementation/`](../implementation/) |
| 23–25 | Where this goes, and the gaps | The gap list below. | — |

## Tear down

Stop all charges when the demo is over:

```bash
cd infrastructure/gcp
terraform destroy -var="project_id=YOUR_PROJECT_ID"
```

To pause between rehearsals without destroying the setup, scale the node pool to zero. Volumes are kept; Kafka and the services come back when the node does:

```bash
gcloud container clusters resize cho-demo --node-pool demo --num-nodes 0 --location us-west1-a
# resume: --num-nodes 1
```

On Autopilot, which bills for running pods rather than nodes, scale the workloads instead:

```bash
kubectl scale deployment --all -n cloudhealthoffice --replicas=0
kubectl scale statefulset --all -n cloudhealthoffice --replicas=0      # MongoDB and Postgres keep their volumes
kubectl delete -f deploy/gcp/kafka/kafka-cluster.yaml                   # Kafka holds no demo state worth keeping
```

To bring everything back, rerun the deploy workflow with the existing image tag. It recreates Kafka and restores replica counts.

**Cost.** The default single e2-standard-8 node (8 vCPU, 32 GB) runs about $7 a day including the cluster fee (Google's free tier credits one zonal cluster's management fee), plus about 165 GB of standard disk and image storage. Autopilot with the full resource profile is roughly $15 a day. Set the budget alert.

**Why one node by default.** A new billing account starts with 12 vCPU, 250 GB of SSD and 4 external IPs, and Google declines increases until the account has billing history. The default cluster, the `small` resource profile (lower CPU requests, one replica per service; limits unchanged) and standard persistent disk fit inside those limits. Once quota is raised, set `autopilot = true` (or a larger `node_machine_type` / `node_count`) and deploy with `resource_profile: default` for benchmark runs. The live MCC run in the demo is a small sample either way.

## What this is and isn't

**It is:**
- Cloud Health Office's services running on Google Cloud with no application code changes. The same manifests drive AKS, local Kubernetes and GKE.
- Kafka, MongoDB and Redis running in the cluster, with keyless GitHub Actions deploys.
- Token authentication enforced on every API, using development-signed tokens.
- Services running as `Development` hosts (claims-service, provider-service and fhir-service as `Testing` hosts). A Development or Testing host refuses to start on Kubernetes unless an override says it is deliberate. The renderer sets `CHO_ALLOW_DEV_ON_DEPLOYED_HOST=true` (and `CHO_TESTING=1`, `CHO_ALLOW_TESTING_ENVIRONMENT=true`) on every Deployment, and each pod logs a warning on startup. Development hosts return exception details in error responses. That is acceptable only because the cluster holds synthetic data and is not publicly reachable.

**It isn't:**
- **A production or HIPAA environment.** Production on Google Cloud would need:
  - a Google BAA
  - customer-managed encryption keys
  - a private cluster with Private Service Connect
  - asymmetric token signing through token-service (with a Google workload identity path)
  - Google's managed services for state: Managed Service for Apache Kafka (configuration only, no code change), Cloud Storage behind the document store interface, and Google Secret Manager or HashiCorp Vault (the Vault provider already exists) behind the secret provider interface

  Estimate: about 5–7 engineer-weeks.
- **A live connection to a legacy core.** QNXT, Facets and HealthEdge adapters are interfaces with stubs, built per engagement. Augment-mode comparison needs that adapter to supply the core's results.
- **Production capacity evidence.** Benchmarks are reproducible local runs.

## Troubleshooting

| Symptom | Check |
|---|---|
| Pod in `CreateContainerConfigError` | A secret or key it references is missing: `kubectl describe pod <pod> -n cloudhealthoffice`. Add it to `scripts/deploy-gke.sh`. |
| Service crash-loops at startup | `kubectl logs deployment/<name> -n cloudhealthoffice`. A `ChoAuth` error means its host environment or `cho-service-tokens` didn't apply. |
| Kafka not ready | `kubectl get kafka,kafkanodepool,pods -n kafka`. On Autopilot, capacity can take a few minutes to arrive. On the Standard cluster, `kubectl describe pod` showing `Insufficient cpu` or `memory` means the node is full: deploy with the `small` profile or add a node. |
| `401` from every API | The token's tenant must match the `X-Tenant-ID` header (`TENANT_ID`, default `demo`). Tokens expire after 12 hours. |
| Port-forward drops | `port-forward.sh` restarts forwards automatically; rerun it if the terminal closed. |
