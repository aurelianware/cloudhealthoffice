# Cloud Health Office Quick Start

This guide is the source-repository quick start for Cloud Health Office. It
focuses on what is currently usable and verifiable:

- run the platform locally with Docker Compose or Docker Desktop Kubernetes
- submit and adjudicate claims against local services
- run a scored Million Claim Challenge validation
- inspect the Mass Adjudication console
- compare your results with the published 100,000-claim local evidence

Cloud Health Office is source-available software that you can download and run
in your own environment. The public hosted portal and public hosted API domains
are not currently deployed as self-service products, so this guide does not
depend on `portal.cloudhealthoffice.com` or `api.cloudhealthoffice.com`.

## Current Evidence Snapshot

The canonical published result is **Episode 015** of the Million Claim
Challenge — a full one-million-claim run:

- 1,000,000 local Docker Desktop Kubernetes claims processed
- 0 platform failures
- 129,981/130,000 workflow checks matched (19 mismatched, 0 unsupported,
  0 observation timeouts)
- 20,000/20,000 comparable payments within $0.01
- 123.81 claims/second during the timed claim-processing phase

Episode 016 reached a higher sustained throughput on the same corpus —
155.89 claims/second, P95 910 ms, P99 1,205 ms — but left 20 workflow checks
unreconciled inside the run artifact, so Episode 015 remains the strict
zero-failure baseline.

The earlier **100,000-claim rung (Episode 008)** is still a useful smaller
target to compare against:

- 100,000 claims processed, 0 platform failures
- 0 scoreable workflow mismatches, 0 unexpected pends
- 2,000/2,000 comparable payments within $0.01, $0.00 average and maximum delta
- 55.40 claims/second during the timed claim-processing phase
- 324 ms P95 and 480 ms P99 latency

Scope matters: all of this is local engineering evidence, not a production
cloud capacity claim. Throughput is hardware-dependent and will not reproduce
exactly on different hardware; the correctness gates are seed-reproducible.
Note also that the Episode 008 Kubernetes job took about 38 minutes end to end
while timed processing accounted for about 30 of those, because run-scoped
fixture preparation dominates the rest of the lifecycle.

Read the artifacts:

- [Episode 015 benchmark results](../million-claim-challenge/podcast/episode-015/benchmark-results.txt) — canonical 1M baseline
- [Episode 015 raw evidence](../million-claim-challenge/podcast/episode-015/raw-evidence-1m-runs.txt)
- [Episode 008 benchmark results](../million-claim-challenge/podcast/episode-008/benchmark-results.txt) — 100K rung
- [Episode 008 article draft](../million-claim-challenge/podcast/episode-008/article.txt)
- [Benchmark methodology](../benchmarks/README.md)
- [Public evidence archive](https://cloudhealthoffice.com/docs/million-claim-challenge/evidence#episode-008)

## Option 1: Docker Compose Core Stack

Use Docker Compose when you want the quickest local service loop without
Kubernetes.

```bash
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

docker compose --profile core up -d

curl http://localhost:5001/health/live
```

Notes:

- `docker compose up -d` with no profile starts only infrastructure such as
  MongoDB and Redis.
- `--profile core` starts the core adjudication services.
- Additional profiles are documented at the top of
  [`docker-compose.yml`](../../docker-compose.yml).
- For the broader development stack, use
  [`docker-compose.development.yml`](../../docker-compose.development.yml).

Common local ports:

| Service | Local URL |
| --- | --- |
| Claims service | `http://localhost:5001` |
| Benefit plan service | `http://localhost:5002` |
| Member service | `http://localhost:5003` |
| Provider service | `http://localhost:5004` |
| MongoDB | `localhost:27017` |

## Option 2: Local Kubernetes

Use Docker Desktop Kubernetes when you want the production-shaped local
environment used by the Million Claim Challenge work.

Prerequisites:

- Docker Desktop 4.x+
- Kubernetes enabled in Docker Desktop
- `kubectl`
- `bash`
- `curl`
- `jq` optional, but useful

Recommended Docker Desktop allocation:

- small runs: at least 6 CPU and 16 GB memory
- 100K validation: 18 CPU and about 24 GB memory

Deploy:

```bash
git clone https://github.com/aurelianware/cloudhealthoffice.git
cd cloudhealthoffice

kubectl config current-context
kubectl cluster-info

bash ./scripts/deploy-local.sh
```

The deploy script builds local images, creates the `cloudhealthoffice`
namespace, deploys MongoDB and Redis, creates local secrets, applies service
manifests, and waits for core services.

**Expect 30-60 minutes on a first run** with a cold Docker cache — it builds an
image per service. Subsequent runs are much faster, and `--skip-build` skips the
build entirely.

**The namespace is fixed.** `deploy-local.sh` deploys to `cloudhealthoffice`,
and the service manifests name that namespace explicitly, so you cannot point
the script at a different one with an environment variable. If you already have
a deployment there and want a clean environment, delete it first:

```bash
kubectl delete namespace cloudhealthoffice
```

That destroys the local MongoDB volume and any Mass Adjudication run history in
it. `run-mcc-local-k8s.sh` does honour a `NAMESPACE` variable, but it has to
match wherever the services were actually deployed.

No secret store is required. The script sets `SecretProvider__Provider=None` and
provisions local Kubernetes Secrets itself; you do not need Vault, Azure Key
Vault, or a `.env` file to complete this walkthrough. See
[SECRET-MANAGEMENT.md](../security/SECRET-MANAGEMENT.md) for the production
options.

For repeat deploys after images are already built:

```bash
bash ./scripts/deploy-local.sh --skip-build
```

Verify:

```bash
kubectl get deployments -n cloudhealthoffice
kubectl get pods -n cloudhealthoffice
```

Open local access in separate terminals:

```bash
kubectl port-forward -n cloudhealthoffice svc/portal 8080:80
kubectl port-forward -n cloudhealthoffice svc/claims-service 5001:80
kubectl port-forward -n cloudhealthoffice svc/benefit-plan-service 5002:80
kubectl port-forward -n cloudhealthoffice svc/payment-service 5006:80
kubectl port-forward -n cloudhealthoffice svc/fhir-service 5080:80
```

Then open:

- Portal: `http://localhost:8080`
- Mass Adjudication console: `http://localhost:8080/mass-adjudication-runs`
- Claims Swagger: `http://localhost:5001/swagger`

API calls to the seeded local tenant use:

```text
X-Tenant-ID: demo
```

The website version of this walkthrough is maintained at
[cloudhealthoffice.com/docs/quickstart](https://cloudhealthoffice.com/docs/quickstart),
with deeper Kubernetes notes at
[cloudhealthoffice.com/docs/quickstart-kubernetes](https://cloudhealthoffice.com/docs/quickstart-kubernetes).

## Seed and Smoke-Test Claims

After port-forwarding the claims and benefit-plan services:

```bash
CLAIMS_URL=http://localhost:5001 \
BENEFIT_URL=http://localhost:5002 \
./scripts/seed-local.sh --tenant demo
```

The script seeds local reference data, submits a professional claim, runs
adjudication, and prints the claim ID, allowed amount, plan payment, and member
responsibility.

To exercise the stricter human-examiner lifecycle, add these port-forwards in
separate terminals:

```bash
kubectl port-forward -n cloudhealthoffice svc/member-service 5003:80
kubectl port-forward -n cloudhealthoffice svc/provider-service 5004:80
kubectl port-forward -n cloudhealthoffice svc/enrollment-import-service 5011:80
```

Then run:

```bash
./scripts/smoke/837-pended-claim-e2e-smoke.sh
```

This test provisions an isolated tenant, imports an 834, submits a real
two-line 837 that triggers an NCCI bundling edit, verifies the claim appears in
the work queue, and resolves it through the examiner endpoint. It fails closed
unless local AI examination is disabled, preventing accidental metered API use.

## Run a Scored MCC Validation

Start small. A 1,000-claim run is enough to verify the pipeline, result
publishing, workflow scoring, pend observation, and payment gate.

```bash
CLAIMS=1000 \
MAX_CLAIMS=1000 \
PARALLELISM=10 \
PROGRESS_EVERY=100 \
JOB_NAME=mcc-quickstart-1k \
./scripts/run-mcc-local-k8s.sh
```

The validator generates deterministic synthetic claims, prepares run-scoped
fixtures, submits and adjudicates claims, observes expected pends, checks for
unexpected pends in scoreable non-pend scenarios, scores workflow outcomes,
checks comparable payments, and publishes the run summary to claims-service.

A clean run should report:

- **0 platform failures** — this one is absolute. Any platform failure is a bug.
- **0 unexpected pends**
- **0 payment mismatches**
- **0 observation timeouts** at this size

Unsupported scenarios are reported separately. They are named product gaps, not
passes and not platform failures.

**On workflow mismatches:** a small number is expected and is not a platform
failure. The canonical 1,000,000-claim baseline (Episode 015) itself reports
129,981/130,000 matched — 19 mismatched. Mismatches concentrate in
coordination-of-benefits edge cases, which are Phase-1 scoped. Treat a
mismatch count that is small and confined to `EdgeCase:Cob*` scenarios as
consistent with the published baseline; treat a *platform failure*, a payment
mismatch, or an unexpected pend as a real regression.

Reference points from clean local runs, for orientation only — throughput is
hardware-dependent and will not reproduce on different hardware:

| Run | Processed | Platform failures | Workflow checks | Payment gate | Throughput |
| --- | --- | --- | --- | --- | --- |
| 1K (clean-clone run A) | 1,000 | 0 | 130/130 | 20/20 exact | 191.69 claims/sec |
| 1K (clean-clone run B) | 1,000 | 0 | 130/130 | 20/20 exact | 169.65 claims/sec |
| 10K (clean-clone run A) | 10,000 | 0 | 1,297/1,300 | 200/200 exact | 388.65 claims/sec |
| 10K (clean-clone run B) | 10,000 | 0 | 1,300/1,300 | 200/200 exact | 406.91 claims/sec |
| 1M (Episode 015) | 1,000,000 | 0 | 129,981/130,000 | 20,000/20,000 exact | 123.81 claims/sec |

Note the two 10K runs: the same seed produced 3 `EdgeCase:CobPrimaryPayer`
mismatches on run A and none on run B. Run A was executed against a deployment
where the demo-data seed had silently failed, so the likeliest explanation is
environment state rather than the validator. It is recorded here rather than
smoothed over, because "correctness gates are seed-reproducible" is a claim the
benchmark makes, and a mismatch count that moves between runs is worth
investigating before it is quoted to anyone.

Small runs show *higher* claims/sec than the 1M run because they never reach
sustained load. Do not read a small-run figure as a platform throughput number.

Inspect the job directly if needed:

```bash
kubectl get job -n cloudhealthoffice mcc-quickstart-1k
kubectl logs -n cloudhealthoffice job/mcc-quickstart-1k
```

Inspect the published run in the portal:

```text
http://localhost:8080/mass-adjudication-runs
```

Use the console to review:

- processed claim count
- claims/second
- P95 and P99 latency
- paid, pended, and business-denial outcome mix
- platform failures
- matched, mismatched, unsupported, and timed-out workflow checks
- expected-pend and unexpected-pend evidence
- payment gate results
- retained claim-level evidence

## Known Issues On A Fresh Local Deploy

These are current, reproduced on a clean clone, and do not affect the Million
Claim Challenge:

- **`fhir-service` does not become ready.** It runs SMART trust in `Demo` mode
  and resolves its trusted issuer from `SmartAuth:Issuer`, which defaults to the
  public `https://auth.cloudhealthoffice.com` rather than the in-cluster
  `smart-auth-service`. Its readiness probe reports
  `No trusted issuer has usable signing keys` and it stays `0/1`. The
  adjudication pipeline, portal and MCC are unaffected, but the FHIR/CMS-0057-F
  surfaces are not exercisable from a default local deploy.

  `smart-auth-service` itself is **fixed** by this change and reaches `1/1`;
  it previously failed for a different reason (the database-name casing
  conflict) and is no longer a known issue.

- **`idcard-service — manifest has no Kubernetes objects`** is harmless noise.
  `src/services/idcard-service/k8s/idcard-service-deployment.yaml` is an alias
  stub pointing at `/k8s/idcard-service.yaml`, which the script applies
  separately. The service does deploy.

If the demo tenant seed reports a failure, re-run `deploy-local.sh`. MongoDB
occasionally is not ready for authenticated connections at that point; the
script now waits and retries, but a slow machine can still exceed the window.

## Scale Up Carefully

Use 10K as a confidence gate before attempting 100K.

```bash
CLAIMS=10000 MAX_CLAIMS=10000 PARALLELISM=10 PROGRESS_EVERY=1000 \
JOB_NAME=mcc-quickstart-10k ./scripts/run-mcc-local-k8s.sh
```

Only attempt 100K after smaller runs are clean and the portal can load the
published run:

```bash
CLAIMS=100000 MAX_CLAIMS=100000 PARALLELISM=10 PROGRESS_EVERY=5000 \
JOB_NAME=mcc-quickstart-100k ./scripts/run-mcc-local-k8s.sh
```

Do not calculate claims/second from total Kubernetes job duration. The validator
separates timed claim processing from fixture generation and seeding. For the
published clean 100K result, timed processing finished in under 30 minutes, but
the full Kubernetes job took more than two hours because fixture preparation
dominated the lifecycle.

## Deployment Posture

There is no currently supported one-click Azure deployment from this quick
start. Older docs referenced a root `azuredeploy.json`, but that artifact is not
present in the current repository.

Current practical paths:

- run locally with Docker Compose
- run locally with Docker Desktop Kubernetes
- adapt the Kubernetes manifests and deployment scripts for your own
  non-production environment
- use the source-available APIs and services in an environment you control

Azure infrastructure templates and operational references live under
[`infrastructure/`](../../infrastructure/), but production deployment requires
environment-specific review: identity, ingress, TLS, secrets, monitoring,
backup/restore, PHI logging controls, data retention, and payer-specific
integration configuration.

For commercial evaluation or a guided pilot, use:

- [Contact](https://cloudhealthoffice.com/contact)
- [Book a product demo](https://cloudhealthoffice.com/demo)
- [Positioning and evidence notes](../POSITIONING.md)

## What This Guide Does Not Claim

This guide does not claim:

- a hosted SaaS portal is available for self-service signup
- public `portal.cloudhealthoffice.com` or `api.cloudhealthoffice.com` endpoints
  are deployed
- the clean 100K local result is a production-cloud capacity claim
- every edge-case scenario is scoreable today

The current proof ladder is evidence-first: make outcomes observable, keep
unsupported scenarios separate from failures and wins, publish raw benchmark
artifacts, and increase volume only after correctness gates remain clean.

## Troubleshooting

Check cluster health:

```bash
kubectl get pods -n cloudhealthoffice
kubectl describe pod -n cloudhealthoffice <pod-name>
kubectl logs -n cloudhealthoffice deploy/claims-service --tail=100
```

Restart a service:

```bash
kubectl rollout restart deployment/claims-service -n cloudhealthoffice
kubectl rollout status deployment/claims-service -n cloudhealthoffice
```

Common issues:

| Symptom | Check |
| --- | --- |
| Portal cannot load runs | Confirm claims-service port-forward or in-cluster service is healthy |
| Claims API 401/tenant errors | Include `X-Tenant-ID: demo` for direct local API calls |
| MCC job finishes but no console run appears | Check `PUBLISH_DASHBOARD` defaults and claims-service connectivity in job logs |
| Slow 100K lifecycle | Separate fixture preparation duration from timed processing duration |
| Payment mismatches | Inspect expected payment, actual payment, tolerance, and line adjudication |

## Additional Resources

- [Repository README](../../README.md)
- [Website Quick Start](https://cloudhealthoffice.com/docs/quickstart)
- [Kubernetes Reference](https://cloudhealthoffice.com/docs/quickstart-kubernetes)
- [Architecture overview](../architecture/ARCHITECTURE.md)
- [Deployment guide](DEPLOYMENT.md)
- [Million Claim Challenge evidence archive](https://cloudhealthoffice.com/docs/million-claim-challenge/evidence)
- [Episode 008 benchmark results](../million-claim-challenge/podcast/episode-008/benchmark-results.txt)
